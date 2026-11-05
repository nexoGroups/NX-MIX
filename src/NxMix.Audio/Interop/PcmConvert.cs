using System.Runtime.CompilerServices;

namespace NxMix.Audio.Interop;

internal enum SampleKind { Float32, Int16, Int24, Int32 }

/// <summary>Umwandlung zwischen Geraete-Samples (verschachtelt, beliebiges Format) und float. Allokationsfrei.
/// Beim Schreiben wird hart auf +-1.0 begrenzt und NaN/Inf zu 0 gemacht (letzte Sicherung vor dem Geraet).</summary>
internal static unsafe class PcmConvert
{
    public static int BytesPerSample(SampleKind kind) => kind switch
    {
        SampleKind.Float32 => 4,
        SampleKind.Int16 => 2,
        SampleKind.Int24 => 3,
        _ => 4,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sanitize(float v)
    {
        if (!float.IsFinite(v)) return 0f;
        if (v > 1f) return 1f;
        if (v < -1f) return -1f;
        return v;
    }

    /// <summary>Liest Kanal <paramref name="channel"/> aus einem verschachtelten Geraetepuffer in <paramref name="dst"/>[f * dstStride].
    /// NaN/Inf werden zu 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void ReadChannel(byte* src, int frames, int blockAlign, int channel, SampleKind kind, float* dst, int dstStride)
    {
        byte* p = src + channel * BytesPerSample(kind);
        switch (kind)
        {
            case SampleKind.Float32:
                for (int f = 0; f < frames; f++)
                {
                    float v = *(float*)p;
                    dst[f * dstStride] = float.IsFinite(v) ? v : 0f;
                    p += blockAlign;
                }
                break;
            case SampleKind.Int16:
                for (int f = 0; f < frames; f++)
                {
                    dst[f * dstStride] = *(short*)p * (1f / 32768f);
                    p += blockAlign;
                }
                break;
            case SampleKind.Int24:
                for (int f = 0; f < frames; f++)
                {
                    int v = (p[0] << 8) | (p[1] << 16) | (p[2] << 24);
                    dst[f * dstStride] = (v >> 8) * (1f / 8388608f);
                    p += blockAlign;
                }
                break;
            default:
                for (int f = 0; f < frames; f++)
                {
                    dst[f * dstStride] = *(int*)p * (1f / 2147483648f);
                    p += blockAlign;
                }
                break;
        }
    }

    /// <summary>Schreibt ein planares float-Signal in Kanal <paramref name="channel"/> eines verschachtelten Geraetepuffers.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void WriteChannel(float* src, int frames, byte* dst, int blockAlign, int channel, SampleKind kind)
    {
        byte* p = dst + channel * BytesPerSample(kind);
        switch (kind)
        {
            case SampleKind.Float32:
                for (int f = 0; f < frames; f++)
                {
                    *(float*)p = Sanitize(src[f]);
                    p += blockAlign;
                }
                break;
            case SampleKind.Int16:
                for (int f = 0; f < frames; f++)
                {
                    float v = Sanitize(src[f]) * 32767f;
                    *(short*)p = (short)(int)(v >= 0f ? v + 0.5f : v - 0.5f);
                    p += blockAlign;
                }
                break;
            case SampleKind.Int24:
                for (int f = 0; f < frames; f++)
                {
                    float v = Sanitize(src[f]) * 8388607f;
                    int i = (int)(v >= 0f ? v + 0.5f : v - 0.5f);
                    p[0] = (byte)i;
                    p[1] = (byte)(i >> 8);
                    p[2] = (byte)(i >> 16);
                    p += blockAlign;
                }
                break;
            default:
                for (int f = 0; f < frames; f++)
                {
                    double v = Sanitize(src[f]) * 2147483647.0;
                    *(int*)p = (int)(v >= 0.0 ? v + 0.5 : v - 0.5);
                    p += blockAlign;
                }
                break;
        }
    }
}
