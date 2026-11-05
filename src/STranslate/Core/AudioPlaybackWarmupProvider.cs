using NAudio.Wave;

namespace STranslate.Core;

/// <summary>
/// 在解码后的音频前输出静音，不修改原始数据或音频格式，也不负责释放源读取器。
/// </summary>
internal sealed class AudioPlaybackWarmupProvider : IWaveProvider
{
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    private readonly IWaveProvider _source;
    private readonly byte _silenceValue;
    private long _remainingSilenceBytes;

    private AudioPlaybackWarmupProvider(IWaveProvider source, int warmupMs)
    {
        _source = source;
        // 按完整采样帧向上取整，保证静音和正文交界处的声道及采样对齐。
        var silenceFrames = ((long)WaveFormat.SampleRate * warmupMs + 999) / 1000;
        _remainingSilenceBytes = silenceFrames * WaveFormat.BlockAlign;
        // 8 位 PCM 的静音中点为 128，其余整数 PCM 和浮点 PCM 的静音为 0。
        _silenceValue = WaveFormat.BitsPerSample == 8 ? (byte)128 : (byte)0;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public static IWaveProvider Create(IWaveProvider source, int warmupMs)
    {
        ArgumentNullException.ThrowIfNull(source);
        warmupMs = Math.Clamp(warmupMs, 0, 5000);
        // 压缩 WAV 等非线性格式不能直接插入 PCM 静音，保持原有播放路径。
        if (warmupMs == 0 || !SupportsSilence(source.WaveFormat))
            return source;

        return new AudioPlaybackWarmupProvider(source, warmupMs);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
            throw new ArgumentException("缓冲区范围无效");

        if (count == 0)
            return 0;

        var silenceBytes = (int)Math.Min(_remainingSilenceBytes, count);
        if (silenceBytes > 0)
        {
            buffer.AsSpan(offset, silenceBytes).Fill(_silenceValue);
            _remainingSilenceBytes -= silenceBytes;
        }

        if (silenceBytes == count)
            return silenceBytes;

        return silenceBytes + _source.Read(buffer, offset + silenceBytes, count - silenceBytes);
    }

    private static bool SupportsSilence(WaveFormat format)
    {
        var isPcm = format.Encoding == WaveFormatEncoding.Pcm;
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;
        if (format is WaveFormatExtensible extensible)
        {
            isPcm = extensible.SubFormat == PcmSubFormat;
            isFloat = extensible.SubFormat == FloatSubFormat;
        }

        return format.SampleRate > 0 && format.BlockAlign > 0 &&
            ((isPcm && format.BitsPerSample is 8 or 16 or 24 or 32) ||
             (isFloat && format.BitsPerSample is 32 or 64));
    }
}
