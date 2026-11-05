using System.Runtime.CompilerServices;
using NxMix.Audio.Interop;

namespace NxMix.Tests.Audio;

public unsafe class PcmConvertTests
{
    [Fact]
    public void ComStructSizes_MatchTheWindowsHeaders()
    {
        Assert.Equal(18, Unsafe.SizeOf<WaveFormatEx>());
        Assert.Equal(40, Unsafe.SizeOf<WaveFormatExtensible>());
        Assert.Equal(24, Unsafe.SizeOf<PropVariant>());
    }

    private static byte[] Write(float[] src, SampleKind kind, int channels, int channel)
    {
        int bps = PcmConvert.BytesPerSample(kind);
        var dst = new byte[src.Length * bps * channels];
        fixed (float* s = src)
        fixed (byte* d = dst)
            PcmConvert.WriteChannel(s, src.Length, d, channels * bps, channel, kind);
        return dst;
    }

    private static float[] Read(byte[] src, SampleKind kind, int channels, int channel, int frames)
    {
        int bps = PcmConvert.BytesPerSample(kind);
        var dst = new float[frames];
        fixed (byte* s = src)
        fixed (float* d = dst)
            PcmConvert.ReadChannel(s, frames, channels * bps, channel, kind, d, 1);
        return dst;
    }

    [Fact]
    public void Float32_ClampsAndRemovesNaN()
    {
        var bytes = Write(new[] { 0.25f, 2f, -3f, float.NaN, float.PositiveInfinity, float.NegativeInfinity }, SampleKind.Float32, 1, 0);
        float[] back = Read(bytes, SampleKind.Float32, 1, 0, 6);
        Assert.Equal(new[] { 0.25f, 1f, -1f, 0f, 0f, 0f }, back);
    }

    [Fact]
    public void Int16_FullScaleAndRounding()
    {
        var bytes = Write(new[] { 1f, -1f, 0f, 0.5f, 7f }, SampleKind.Int16, 1, 0);
        var s = new short[5];
        Buffer.BlockCopy(bytes, 0, s, 0, bytes.Length);
        Assert.Equal(new short[] { 32767, -32767, 0, 16384, 32767 }, s);
    }

    [Fact]
    public void Int32_FullScaleNeverWrapsToNegative()
    {
        // Gefahr: 1.0f * 2147483648f ueberlaeuft zu int.MinValue (lauter Knack). Muss +int.MaxValue sein.
        var bytes = Write(new[] { 1f, 5f, -1f, -9f, 0f }, SampleKind.Int32, 1, 0);
        var s = new int[5];
        Buffer.BlockCopy(bytes, 0, s, 0, bytes.Length);
        Assert.Equal(new[] { int.MaxValue, int.MaxValue, -int.MaxValue, -int.MaxValue, 0 }, s);
    }

    [Fact]
    public void Int24_RoundTripWithinOneLsb_AndSignHandling()
    {
        float[] src = { 0f, 0.5f, -0.5f, 1f, -1f, 0.123456f };
        var bytes = Write(src, SampleKind.Int24, 1, 0);
        Assert.Equal(src.Length * 3, bytes.Length);
        float[] back = Read(bytes, SampleKind.Int24, 1, 0, src.Length);
        for (int i = 0; i < src.Length; i++) Assert.True(Math.Abs(back[i] - src[i]) <= 1.5f / 8388608f, $"i={i}: {back[i]} vs {src[i]}");
        // 0x800000 ist -1,0
        var neg = new byte[] { 0x00, 0x00, 0x80 };
        Assert.Equal(-1f, Read(neg, SampleKind.Int24, 1, 0, 1)[0]);
    }

    [Fact]
    public void Int16Read_NegativeFullScale()
    {
        var neg = new byte[] { 0x00, 0x80 };
        Assert.Equal(-1f, Read(neg, SampleKind.Int16, 1, 0, 1)[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void WritingOneChannel_LeavesOthersUntouched(int kindIndex)
    {
        var kind = (SampleKind)kindIndex;
        int bps = PcmConvert.BytesPerSample(kind);
        const int channels = 4, frames = 8;
        var buf = new byte[frames * channels * bps]; // alles Nullen
        var src = new float[frames];
        Array.Fill(src, 0.5f);
        fixed (float* s = src)
        fixed (byte* d = buf)
            PcmConvert.WriteChannel(s, frames, d, channels * bps, 2, kind);

        for (int ch = 0; ch < channels; ch++)
        {
            float[] back = Read(buf, kind, channels, ch, frames);
            if (ch == 2) Assert.All(back, v => Assert.InRange(v, 0.499f, 0.501f));
            else Assert.All(back, v => Assert.Equal(0f, v));
        }
    }
}
