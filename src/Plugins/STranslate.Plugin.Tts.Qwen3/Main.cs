using STranslate.Plugin.Tts.Qwen3.View;
using STranslate.Plugin.Tts.Qwen3.ViewModel;
using System.IO;
using System.Media;
using System.Windows.Controls;

namespace STranslate.Plugin.Tts.Qwen3;

public class Main : ITtsPlugin
{
    private Control? _settingUi;
    private SettingsViewModel? _viewModel;
    private Settings Settings { get; set; } = null!;
    private IPluginContext Context { get; set; } = null!;

    public Control GetSettingUI()
    {
        _viewModel ??= new SettingsViewModel(Context, Settings);
        _settingUi ??= new SettingsView { DataContext = _viewModel };
        return _settingUi;
    }

    public void Init(IPluginContext context)
    {
        Context = context;
        Settings = context.LoadSettingStorage<Settings>();
    }

    public void Dispose() => _viewModel?.Dispose();

    public async Task PlayAudioAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var baseUrl = (Settings.BaseUrl ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl))
            throw new InvalidOperationException("Qwen3-TTS BaseUrl 未配置");

        var url = baseUrl + "/tts";

        // 与 qwen3-tts-server 的 TtsRequest 保持字段一致
        var body = new
        {
            text,
            language = string.IsNullOrWhiteSpace(Settings.Language) ? "Auto" : Settings.Language,
            speaker = Settings.Speaker,
            instruct = string.IsNullOrWhiteSpace(Settings.Instruct) ? null : Settings.Instruct,
        };

        var options = new Options
        {
            ContentType = "application/json",
            Timeout = TimeSpan.FromSeconds(Math.Max(5, Settings.TimeoutSeconds)),
        };

        var audio = await Context.HttpService.PostAsBytesAsync(url, body, options, cancellationToken);
        if (audio == null || audio.Length == 0)
            throw new InvalidOperationException("Qwen3-TTS 返回了空音频");

        // STranslate 核心 AudioPlayer 仅支持 MP3，而服务端返回 WAV，
        // 因此这里绕过核心播放器，使用 System.Media.SoundPlayer 直接同步播 WAV。
        await PlayWavAsync(audio, cancellationToken);
    }

    private static Task PlayWavAsync(byte[] wavBytes, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            using var ms = new MemoryStream(wavBytes);
            using var player = new SoundPlayer(ms);
            using var ctr = cancellationToken.Register(player.Stop);
            cancellationToken.ThrowIfCancellationRequested();
            // PlaySync 会阻塞直至播放结束；Stop() 会立刻返回控制权。
            player.PlaySync();
            cancellationToken.ThrowIfCancellationRequested();
        }, cancellationToken);
    }
}
