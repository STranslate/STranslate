using NAudio.Wave;
using Microsoft.Extensions.Logging;
using STranslate.Plugin;
using System.IO;

namespace STranslate.Core;

public class AudioPlayer : IAudioPlayer
{
    private readonly ILogger<AudioPlayer> _logger;
    private readonly IHttpService _httpService;
    private readonly Settings _settings;
    private WaveOutEvent? _waveOut;
    private MemoryStream? _audioStream;
    private WaveStream? _audioReader;
    private CancellationTokenSource? _cancellationTokenSource;
    private int _stopping;
    private bool _disposed;

    public AudioPlayer(ILogger<AudioPlayer> logger, IHttpService httpService, Settings settings)
    {
        _logger = logger;
        _httpService = httpService;
        _settings = settings;
    }

    /// <summary>
    /// 当前是否正在播放
    /// </summary>
    public bool IsPlaying => _waveOut?.PlaybackState == PlaybackState.Playing;

    /// <summary>
    /// 播放状态改变事件
    /// </summary>
    public event EventHandler<PlaybackState>? PlaybackStateChanged;

    /// <summary>
    /// 播放进度事件
    /// </summary>
    public event EventHandler<TimeSpan>? PlaybackPositionChanged;

    public async Task PlayAsync(byte[]? audioData, CancellationToken cancellationToken = default)
    {
        if (audioData == null || audioData.Length == 0)
        {
            _logger.LogWarning("音频数据为空");
            return;
        }

        await PlayAsync(new AudioData(audioData, AudioFormat.Auto), cancellationToken);
    }

    public async Task PlayAsync(AudioData audioData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audioData);

        if (audioData.Content == null || audioData.Content.Length == 0)
        {
            _logger.LogWarning("音频数据为空");
            return;
        }

        try
        {
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // 创建内存流
            _audioStream = new MemoryStream(audioData.Content, writable: false);
            // 根据音频格式创建读取器
            _audioReader = AudioReaderFactory.Create(audioData, _audioStream);
            // 创建播放设备
            _waveOut = new WaveOutEvent();
            // 在同一次设备播放中先输出静音，避免预热后重新打开设备。
            var playbackProvider = AudioPlaybackWarmupProvider.Create(_audioReader, _settings.AudioPlaybackWarmupMs);
            _waveOut.Init(playbackProvider);
            // 注册事件
            _waveOut.PlaybackStopped += OnPlaybackStopped;
            // 开始播放
            _waveOut.Play();
            OnPlaybackStateChanged(PlaybackState.Playing);
            _logger.LogInformation("音频播放开始");
            // 启动进度监控
            await MonitorPlaybackProgressAsync(_cancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogTrace("音频播放被取消");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "播放音频时发生错误");
            throw;
        }
        finally
        {
            await StopAsync();
        }
    }

    public async Task PlayAsync(string audioUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(audioUrl))
        {
            _logger.LogWarning("音频URL为空");
            return;
        }

        byte[]? audioData = null;
        try
        {
            _logger.LogInformation("开始下载音频: {AudioUrl}", audioUrl);

            audioData = await _httpService.GetAsBytesAsync(audioUrl, cancellationToken);

            _logger.LogInformation("音频下载完成: {Length} bytes", audioData?.Length ?? 0);
        }
        catch (OperationCanceledException)
        {
            _logger.LogTrace("音频下载被取消");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "下载音频时发生错误: {AudioUrl}", audioUrl);
        }

        await PlayAsync(audioData, cancellationToken);
    }

    /// <summary>
    /// 停止播放
    /// </summary>
    public async Task StopAsync()
    {
        if (Interlocked.CompareExchange(ref _stopping, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var cancellationTokenSource = _cancellationTokenSource;
            _cancellationTokenSource = null;
            cancellationTokenSource?.Cancel();

            var waveOut = _waveOut;
            _waveOut = null;

            if (waveOut != null)
            {
                await Task.Run(() =>
                {
                    waveOut.PlaybackStopped -= OnPlaybackStopped;
                    waveOut.Stop();
                    waveOut.Dispose();
                });
            }

            var audioReader = _audioReader;
            _audioReader = null;
            audioReader?.Dispose();

            var audioStream = _audioStream;
            _audioStream = null;
            audioStream?.Dispose();

            cancellationTokenSource?.Dispose();

            OnPlaybackStateChanged(PlaybackState.Stopped);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "停止音频播放时发生错误");
        }
        finally
        {
            Interlocked.Exchange(ref _stopping, 0);
        }
    }

    /// <summary>
    /// 暂停播放
    /// </summary>
    public void Pause()
    {
        if (_waveOut?.PlaybackState == PlaybackState.Playing)
        {
            _waveOut.Pause();
            OnPlaybackStateChanged(PlaybackState.Paused);
            _logger.LogTrace("音频播放已暂停");
        }
    }

    /// <summary>
    /// 恢复播放
    /// </summary>
    public void Resume()
    {
        if (_waveOut?.PlaybackState == PlaybackState.Paused)
        {
            _waveOut.Play();
            OnPlaybackStateChanged(PlaybackState.Playing);
            _logger.LogTrace("音频播放已恢复");
        }
    }

    /// <summary>
    /// 监控播放进度
    /// </summary>
    private async Task MonitorPlaybackProgressAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   _audioReader != null &&
                   _waveOut?.PlaybackState == PlaybackState.Playing)
            {
                var currentPosition = _audioReader.CurrentTime;
                PlaybackPositionChanged?.Invoke(this, currentPosition);

                await Task.Delay(100, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogTrace("音频播放被取消(Monitor)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "监控播放进度时发生错误");
        }
    }

    /// <summary>
    /// 播放停止事件处理
    /// </summary>
    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        OnPlaybackStateChanged(PlaybackState.Stopped);

        if (e.Exception != null)
        {
            _logger.LogError(e.Exception, "音频播放意外停止");
        }
        else
        {
            _logger.LogTrace("音频播放正常结束");
        }

        try
        {
            _cancellationTokenSource?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// 触发播放状态改变事件
    /// </summary>
    private void OnPlaybackStateChanged(PlaybackState state)
    {
        PlaybackStateChanged?.Invoke(this, state);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _ = Task.Run(async () => await StopAsync());
            _disposed = true;
        }
    }
}
