namespace NxMix.Engine;

/// <summary>Ein Kanal, zwei synchrone Abgriffe. Ein Audio-Schreiber und ein UI-Leser.</summary>
public sealed class SpectrumCapture
{
    readonly float[] _before, _after;
    long _written;

    internal SpectrumCapture(bool input, int channel, int sampleRate)
    {
        Input = input;
        Channel = channel;
        SampleRate = sampleRate;
        WindowSize = sampleRate <= 48000 ? 8192 : sampleRate <= 96000 ? 16384 : 32768;
        _before = new float[WindowSize * 2];
        _after = new float[_before.Length];
    }

    internal bool Input { get; }
    internal int Channel { get; }
    public int SampleRate { get; }
    public int WindowSize { get; }

    internal void Before(ReadOnlySpan<float> samples) => Write(_before, samples);

    internal void After(ReadOnlySpan<float> samples)
    {
        Write(_after, samples);
        Volatile.Write(ref _written, _written + samples.Length);
    }

    void Write(float[] buffer, ReadOnlySpan<float> samples)
    {
        int index = (int)(_written & (buffer.Length - 1));
        int first = Math.Min(samples.Length, buffer.Length - index);
        samples[..first].CopyTo(buffer.AsSpan(index));
        samples[first..].CopyTo(buffer);
    }

    /// <summary>Liest das neueste vollständige Fenster, ohne den Audiothread anzuhalten.</summary>
    public bool TryRead(Span<float> before, Span<float> after, ref long lastFrame)
    {
        long end = Volatile.Read(ref _written);
        if (end < WindowSize || end == lastFrame) return false;
        long start = end - WindowSize;
        Read(_before, start, before[..WindowSize]);
        Read(_after, start, after[..WindowSize]);

        // Einen möglicherweise noch unveröffentlichten Audioblock bei der Überlaufprüfung einrechnen.
        Thread.MemoryBarrier();
        if (Volatile.Read(ref _written) - start > _before.Length - MixerEngine.MaxChunk) return false;
        lastFrame = end;
        return true;
    }

    static void Read(float[] buffer, long start, Span<float> samples)
    {
        int index = (int)(start & (buffer.Length - 1));
        int first = Math.Min(samples.Length, buffer.Length - index);
        buffer.AsSpan(index, first).CopyTo(samples);
        buffer.AsSpan(0, samples.Length - first).CopyTo(samples[first..]);
    }
}
