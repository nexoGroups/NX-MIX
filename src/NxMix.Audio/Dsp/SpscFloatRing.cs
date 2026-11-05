using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace NxMix.Audio.Dsp;

/// <summary>Lock-freier SPSC-Ringpuffer fuer verschachtelte float-Frames (vorallokiert, allokationsfrei im Betrieb).
///
/// Genau EIN Produzent (Write) und genau EIN Konsument (TryPeek/Advance/DropOldest/Read).
/// Ueberlauf: Der Produzent blockiert und verwirft nie selbst; er ueberschreibt einfach die aeltesten Frames.
/// Der Konsument erkennt das (Lag &gt; Kapazitaet bzw. Pruefung nach dem Kopieren) und verwirft dann die aeltesten Frames
/// (DropOldest) - es werden nie halb ueberschriebene (zerrissene) Daten ausgeliefert.
///
/// Zaehler sind monotone 64-Bit-Frame-Positionen (kein Wraparound-Problem); Index = Position &amp; Maske.
///
/// Fuellstandsmessung: Der Produzent fuehrt mit jedem Write das Zeitintegral seiner Schreibposition mit (exakte Ganzzahlen,
/// Seqlock). Daraus kann der Konsument den zeitgewichteten MITTELWERT des Fuellstands zwischen zwei beliebigen Zeitpunkten
/// berechnen - ohne die Phasenlage zwischen Schreib- und Lesetakt (Aliasing der Saegezahnkurve), die eine Momentaufnahme verfaelscht.</summary>
internal sealed class SpscFloatRing
{
    private readonly float[] _buf;
    private readonly int _channels;
    private readonly int _capacity;
    private readonly int _mask;

    // Produzent schreibt _writeReserve (Ende des Bereichs, den er gerade beschreibt) VOR dem Kopieren und _write NACH dem Kopieren.
    private long _writeReserve;
    private long _write;
    private long _read; // nur Konsument schreibt

    // Zeitintegral der Schreibposition, per Seqlock (_seq ungerade = Produzent schreibt gerade)
    private long _seq;
    private Int128 _jw;                  // Integral W dt in Frame*Ticks seit dem ersten Write
    private long _tw = long.MinValue;    // Zeit des letzten Writes (Ticks), MinValue = noch nichts geschrieben

    public SpscFloatRing(int channels, int minCapacityFrames)
    {
        if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
        if (minCapacityFrames < 1 || (long)minCapacityFrames * channels > 1 << 28)
            throw new ArgumentOutOfRangeException(nameof(minCapacityFrames));
        int cap = 16;
        while (cap < minCapacityFrames) cap <<= 1;
        _capacity = cap;
        _mask = cap - 1;
        _channels = channels;
        _buf = new float[cap * channels];
    }

    public int Channels => _channels;

    /// <summary>Kapazitaet in Frames (Zweierpotenz).</summary>
    public int CapacityFrames => _capacity;

    /// <summary>Insgesamt geschriebene Frames (Position des Schreibers).</summary>
    public long TotalWritten => Volatile.Read(ref _write);

    /// <summary>Insgesamt verbrauchte (oder verworfene) Frames (Position des Lesers).</summary>
    public long TotalRead => Volatile.Read(ref _read);

    /// <summary>Anzahl ungelesener Frames. Kann Kapazitaet uebersteigen, wenn der Produzent den Konsumenten ueberrundet hat.</summary>
    public long Lag => Volatile.Read(ref _write) - Volatile.Read(ref _read);

    // ---- Produzent ------------------------------------------------------------------------------------------

    /// <summary>Schreibt <paramref name="frames"/> Frames (verschachtelt). Blockiert nie; ueberschreibt bei Bedarf die aeltesten Daten.</summary>
    public void Write(ReadOnlySpan<float> interleaved, int frames) => Write(interleaved, frames, Stopwatch.GetTimestamp());

    /// <summary>Wie Write, mit explizitem Zeitstempel (Ticks der Uhr, die auch der Konsument benutzt).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Write(ReadOnlySpan<float> interleaved, int frames, long nowTicks)
    {
        if (frames <= 0) return;
        int ch = _channels;
        if (interleaved.Length < frames * ch) throw new ArgumentException("Zu wenige Samples fuer die angegebene Frame-Zahl.", nameof(interleaved));

        long w = _write; // nur der Produzent schreibt _write
        long end = w + frames;
        int skip = frames > _capacity ? frames - _capacity : 0; // mehr als die Kapazitaet: nur die neuesten Frames behalten
        Volatile.Write(ref _writeReserve, end);
        Interlocked.MemoryBarrier(); // Reserve muss vor den Datenschreibzugriffen sichtbar sein

        int count = frames - skip;
        int start = (int)((w + skip) & _mask);
        int first = Math.Min(count, _capacity - start);
        interleaved.Slice(skip * ch, first * ch).CopyTo(_buf.AsSpan(start * ch));
        if (count > first)
            interleaved.Slice((skip + first) * ch, (count - first) * ch).CopyTo(_buf.AsSpan(0, (count - first) * ch));

        // Daten sind kopiert; jetzt Position + Zeitintegral konsistent veroeffentlichen (Seqlock).
        long s = _seq;
        Volatile.Write(ref _seq, s + 1);
        Interlocked.MemoryBarrier();
        if (_tw != long.MinValue && nowTicks > _tw) _jw += (Int128)w * (nowTicks - _tw);
        if (_tw == long.MinValue || nowTicks > _tw) _tw = nowTicks;
        Volatile.Write(ref _write, end); // Release: Daten vor dem Veroeffentlichen
        Volatile.Write(ref _seq, s + 2);
    }

    // ---- Konsument ------------------------------------------------------------------------------------------

    /// <summary>Liefert das Zeitintegral der Schreibposition (Frame*Ticks) zum Zeitpunkt max(nowTicks, Zeit des letzten Writes).
    /// <paramref name="effectiveTicks"/> ist der tatsaechlich verwendete Zeitpunkt. Vor dem ersten Write: Integral 0, Zeit = nowTicks.</summary>
    public bool TryReadWriteIntegral(long nowTicks, out Int128 integral, out long effectiveTicks)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            long s1 = Volatile.Read(ref _seq);
            if ((s1 & 1) != 0) { Thread.SpinWait(4); continue; }
            Int128 jw = _jw;
            long tw = _tw;
            long w = Volatile.Read(ref _write);
            Interlocked.MemoryBarrier();
            long s2 = Volatile.Read(ref _seq);
            if (s1 != s2) continue;

            if (tw == long.MinValue)
            {
                integral = Int128.Zero;
                effectiveTicks = nowTicks;
                return true;
            }
            long t = Math.Max(nowTicks, tw);
            integral = jw + (Int128)w * (t - tw);
            effectiveTicks = t;
            return true;
        }
        integral = Int128.Zero;
        effectiveTicks = nowTicks;
        return false;
    }

    /// <summary>Kopiert die naechsten <paramref name="frames"/> Frames, ohne sie zu verbrauchen. false, wenn nicht genug Daten da sind,
    /// der Produzent den Leser ueberrundet hat oder die Daten waehrend des Kopierens ueberschrieben wurden (dann DropOldest aufrufen).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool TryPeek(Span<float> destination, int frames)
    {
        if (frames <= 0) return true;
        int ch = _channels;
        long r = _read;
        long w = Volatile.Read(ref _write);
        long lag = w - r;
        if (lag < frames || lag > _capacity) return false;

        int start = (int)(r & _mask);
        int first = Math.Min(frames, _capacity - start);
        _buf.AsSpan(start * ch, first * ch).CopyTo(destination);
        if (frames > first)
            _buf.AsSpan(0, (frames - first) * ch).CopyTo(destination.Slice(first * ch));

        Interlocked.MemoryBarrier(); // Daten muessen vor der Pruefung gelesen sein
        return Volatile.Read(ref _writeReserve) - r <= _capacity;
    }

    /// <summary>Verbraucht <paramref name="frames"/> Frames.</summary>
    public void Advance(int frames)
    {
        if (frames > 0) Volatile.Write(ref _read, _read + frames);
    }

    /// <summary>Verwirft die aeltesten Frames, bis nur noch <paramref name="keepFrames"/> uebrig sind. Gibt die Zahl verworfener Frames zurueck.</summary>
    public long DropOldest(int keepFrames)
    {
        long w = Volatile.Read(ref _write);
        long r = _read;
        long newRead = w - Math.Max(0, keepFrames);
        if (newRead <= r) return 0;
        Volatile.Write(ref _read, newRead);
        return newRead - r;
    }

    /// <summary>Bequem: TryPeek + Advance. Gibt <paramref name="frames"/> zurueck oder 0, wenn nichts geliefert werden konnte.</summary>
    public int Read(Span<float> destination, int frames)
    {
        if (!TryPeek(destination, frames)) return 0;
        Advance(frames);
        return frames;
    }
}
