using NAudio.Wave;
using STranslate.Core;

namespace STranslate.Tests;

public class AudioPlaybackWarmupProviderTests
{
    [Theory]
    [InlineData(8000, 1, 8, 128)]
    [InlineData(24000, 1, 16, 0)]
    [InlineData(44100, 2, 24, 0)]
    [InlineData(48000, 2, 32, 0)]
    public void Read_PrependsSilenceAndPreservesEntirePcm(
        int sampleRate, int channels, int bits, byte silenceValue)
    {
        var format = new WaveFormat(sampleRate, bits, channels);
        VerifySilenceAndContent(format, silenceValue);
    }

    [Fact]
    public void Read_PrependsZeroSilenceToFloatPcm()
    {
        VerifySilenceAndContent(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), 0);
    }

    [Theory]
    [InlineData(8, 128)]
    [InlineData(24, 0)]
    [InlineData(32, 0)]
    public void Read_SupportsExtensiblePcmAndFloat(int bits, byte silenceValue)
    {
        VerifySilenceAndContent(new WaveFormatExtensible(48000, bits, 2), silenceValue);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void Create_DisabledWarmupReturnsOriginalProvider(int warmupMs)
    {
        using var source = new RawSourceWaveStream(new MemoryStream(new byte[16]), new WaveFormat(24000, 16, 1));

        Assert.Same(source, AudioPlaybackWarmupProvider.Create(source, warmupMs));
    }

    [Fact]
    public void Create_NonLinearAudioRetainsOriginalProvider()
    {
        using var source = new RawSourceWaveStream(new MemoryStream(new byte[16]), WaveFormat.CreateALawFormat(8000, 1));

        Assert.Same(source, AudioPlaybackWarmupProvider.Create(source, 500));
    }

    [Fact]
    public void Read_ClampsDurationAndDoesNotConsumeSourceDuringWarmup()
    {
        var format = new WaveFormat(8000, 16, 1);
        using var source = new RawSourceWaveStream(new MemoryStream(new byte[16]), format);
        var provider = AudioPlaybackWarmupProvider.Create(source, int.MaxValue);
        var buffer = new byte[format.AverageBytesPerSecond * 5];

        Assert.Equal(buffer.Length, provider.Read(buffer, 0, buffer.Length));
        Assert.Equal(0, source.Position);
        Assert.Equal(16, provider.Read(buffer, 0, buffer.Length));
        Assert.Equal(0, provider.Read(buffer, 0, buffer.Length));
    }

    [Fact]
    public void Read_ZeroLengthDoesNotConsumeSilenceOrSource()
    {
        using var source = new RawSourceWaveStream(new MemoryStream(new byte[16]), new WaveFormat(8000, 8, 1));
        var provider = AudioPlaybackWarmupProvider.Create(source, 1);
        var buffer = new byte[8];

        Assert.Equal(0, provider.Read(buffer, 0, 0));
        Assert.Equal(8, provider.Read(buffer, 0, 8));
        Assert.All(buffer, value => Assert.Equal(128, value));
        Assert.Equal(0, source.Position);
    }

    [Fact]
    public void Read_NewPlaybackGetsNewWarmup()
    {
        var format = new WaveFormat(8000, 8, 1);
        for (var playback = 0; playback < 2; playback++)
        {
            using var source = new RawSourceWaveStream(new MemoryStream(new byte[] { 1, 2 }), format);
            var provider = AudioPlaybackWarmupProvider.Create(source, 1);
            Assert.Equal(Enumerable.Repeat((byte)128, 8).Concat(new byte[] { 1, 2 }), ReadAll(provider));
        }
    }

    [Fact]
    public void Read_RejectsInvalidBufferRangeWithoutConsumingAudio()
    {
        using var source = new RawSourceWaveStream(new MemoryStream(new byte[16]), new WaveFormat(8000, 16, 1));
        var provider = AudioPlaybackWarmupProvider.Create(source, 1);
        var buffer = new byte[16];

        Assert.Throws<ArgumentNullException>(() => provider.Read(null!, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => provider.Read(buffer, -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => provider.Read(buffer, 0, -1));
        Assert.Throws<ArgumentException>(() => provider.Read(buffer, 1, 16));
        Assert.Equal(0, source.Position);
        Assert.Equal(16, provider.Read(buffer, 0, 16));
        Assert.Equal(0, source.Position);
    }

    private static void VerifySilenceAndContent(WaveFormat format, byte silenceValue)
    {
        // 极短正文用于验证首个采样和最后一个采样均未被静音覆盖或截断。
        var content = Enumerable.Range(1, format.BlockAlign * 7).Select(value => (byte)value).ToArray();
        using var source = new RawSourceWaveStream(new MemoryStream(content, writable: false), format);
        var provider = AudioPlaybackWarmupProvider.Create(source, 1);
        var expectedFrames = (format.SampleRate + 999) / 1000;
        var silence = Enumerable.Repeat(silenceValue, expectedFrames * format.BlockAlign);

        Assert.Same(format, provider.WaveFormat);
        Assert.Equal(silence.Concat(content), ReadAll(provider));
        Assert.Equal(content.Length, source.Position);
    }

    private static byte[] ReadAll(IWaveProvider provider)
    {
        // 较小且跨越静音/正文边界的缓冲区，同时验证 offset 之外的数据未被改写。
        var count = provider.WaveFormat.BlockAlign * 3;
        var buffer = new byte[count + 2];
        var result = new List<byte>();
        int read;
        do
        {
            Array.Fill(buffer, (byte)0xAA);
            read = provider.Read(buffer, 1, count);
            Assert.InRange(read, 0, count);
            Assert.Equal(0xAA, buffer[0]);
            Assert.All(buffer.Skip(1 + read), value => Assert.Equal(0xAA, value));
            result.AddRange(buffer.AsSpan(1, read).ToArray());
        } while (read > 0);

        return result.ToArray();
    }
}
