using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace NxMix.Audio.Dsp;

/// <summary>Konsumentenseite einer Takt-Bruecke: liest aus einem SPSC-Ringpuffer (Takt des Produzenten) im Takt des Konsumenten.
/// Kombiniert Ringpuffer, kubischen Resampler, PI-Regler auf den Fuellstand und die Unter-/Ueberlauf-Logik:
///  * Fuellstand = zeitgewichteter Mittelwert (siehe SpscFloatRing), nicht die Momentaufnahme: Die Momentaufnahme haengt von der
///    Phasenlage der beiden Takte ab (Saegezahn-Aliasing, hunderte Frames) und uebertoent die echte Drift um Groessenordnungen.
///  * Unterlauf: genuegend Daten fehlen -> der Rest des Blocks wird zu STILLE (nie Wiederholung/Muell), das Ende des gelieferten Signals
///    wird kurz ausgeblendet, danach wird mit Stille gewartet, bis der Sollfuellstand wieder erreicht ist (dann kurzes Einblenden).
///  * Ueberlauf: zu viele Daten -> die aeltesten Frames werden verworfen.
/// Alle Aufrufe von Read() kommen von EINEM Thread (dem Konsumenten); Statistik-Eigenschaften duerfen von anderen Threads gelesen werden.</summary>
internal sealed class ClockMatchedReader
{
    private const int FadeFrames = 64;

    private readonly SpscFloatRing _ring;
    private readonly CubicResampler _resampler;
    private readonly DriftController _controller;
    private readonly double _nominal;
    private readonly double _outputRate;
    private readonly int _target;
    private readonly int _maxOut;
    private readonly int _highWater;
    private readonly int _scratchFrames;
    private readonly float[] _scratch;
    private readonly bool _countUnderruns;
    private readonly Func<long> _clock;
    private readonly double _ticksPerSecond;
    private readonly long _minWindowTicks;
    private readonly long _warmupTicks;

    private bool _running;
    private int _fadeIn;
    private long _underruns;
    private long _overruns;
    private long _droppedFrames;
    private double _ppm;
    private double _fill;
    private double _c; // aktuelle relative Korrektur

    // Messung des mittleren Fuellstands
    private bool _baseValid;
    private bool _ctlFresh;
    private Int128 _jwPrev, _jrPrev, _accJ;
    private long _tPrev, _accT, _rAfter;

    /// <param name="ring">Quelle (Frames mit <c>ring.Channels</c> Kanaelen, "Slots").</param>
    /// <param name="nominalRatio">Ring-Frames pro Ausgangs-Frame bei exakt gleichen Takten (= Ring-Samplerate / Ausgangs-Samplerate).</param>
    /// <param name="outputRate">Samplerate des Konsumenten (Ausgangsframes pro Sekunde).</param>
    /// <param name="targetFrames">Sollwert des MITTLEREN Fuellstands in Frames.</param>
    /// <param name="maxOutFrames">Groesster Block, der in einem Stueck verarbeitet wird (groessere Aufrufe werden zerlegt).</param>
    /// <param name="countUnderruns">false fuer Quellen, die zeitweise legitim nichts liefern (Loopback bei Stille).</param>
    /// <param name="clock">Zeitquelle in Ticks (dieselbe wie beim Schreiben in den Ring). Standard: Stopwatch.</param>
    /// <param name="ticksPerSecond">Ticks pro Sekunde der Zeitquelle. Standard: Stopwatch.Frequency.</param>
    public ClockMatchedReader(SpscFloatRing ring, double nominalRatio, double outputRate, int targetFrames, int maxOutFrames,
        bool countUnderruns = true, double bandwidthHz = 0.05, Func<long>? clock = null, double ticksPerSecond = 0)
    {
        _ring = ring;
        _nominal = nominalRatio;
        _outputRate = outputRate;
        _target = Math.Max(16, targetFrames);
        _maxOut = Math.Max(1, maxOutFrames);
        _highWater = Math.Min(_target * 3, ring.CapacityFrames * 3 / 4);
        _countUnderruns = countUnderruns;
        _clock = clock ?? Stopwatch.GetTimestamp;
        _ticksPerSecond = ticksPerSecond > 0 ? ticksPerSecond : Stopwatch.Frequency;
        _minWindowTicks = (long)(_ticksPerSecond * 0.001); // Regler-Update fruehestens nach 1 ms Messfenster
        _warmupTicks = (long)(_ticksPerSecond * 0.25);    // Regler startet auf dem Mittelwert der ersten 250 ms
        _resampler = new CubicResampler(ring.Channels);
        _controller = new DriftController(outputRate, _target, bandwidthHz);
        double maxRatio = nominalRatio * 1.02;
        _scratchFrames = (int)Math.Ceiling(_maxOut * maxRatio) + (int)Math.Ceiling(maxRatio) + 8;
        _scratch = new float[_scratchFrames * ring.Channels];
    }

    public SpscFloatRing Ring => _ring;
    public int TargetFrames => _target;
    public long Underruns => Volatile.Read(ref _underruns);
    public long Overruns => Volatile.Read(ref _overruns);
    public long DroppedFrames => Volatile.Read(ref _droppedFrames);
    public bool IsRunning => Volatile.Read(ref _running);

    /// <summary>Aktuelle Korrektur des Verhaeltnisses in ppm (zusaetzlich zum nominalen Verhaeltnis).</summary>
    public double CorrectionPpm => Volatile.Read(ref _ppm);

    /// <summary>Tiefpassgefilterter mittlerer Fuellstand in Frames.</summary>
    public double FilteredFill => Volatile.Read(ref _fill);

    /// <summary>Fuellstand 0..1 relativ zum Doppelten des Sollwerts (0,5 = Sollwert).</summary>
    public double FillFraction => Math.Clamp(FilteredFill / (2.0 * _target), 0.0, 1.0);

    /// <summary>Liest <paramref name="frames"/> Frames im Takt des Konsumenten nach <paramref name="dst"/>[slot][offset..].
    /// Schreibt IMMER alle Frames (bei Unterlauf Stille). dst.Length muss mindestens ring.Channels sein.</summary>
    public void Read(float[][] dst, int offset, int frames)
    {
        int done = 0;
        while (done < frames)
        {
            int n = Math.Min(frames - done, _maxOut);
            ReadChunk(dst, offset + done, n);
            _rAfter = _ring.TotalRead; // Leseposition fuer die Fuellstandsmessung nach diesem Block
            done += n;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ReadChunk(float[][] dst, int off, int n)
    {
        long lag = _ring.Lag;
        if (lag > _highWater)
        {
            // Ueberlauf (Konsument war zu lange weg oder Drift ausserhalb der Regelreserve): aelteste Frames verwerfen.
            long dropped = _ring.DropOldest(_target);
            Interlocked.Increment(ref _overruns);
            Interlocked.Add(ref _droppedFrames, dropped);
            lag = _ring.Lag;
            _baseValid = false;
        }

        if (!_running)
        {
            if (lag < _target)
            {
                Silence(dst, off, n);
                return;
            }
            if (lag > _target + n) _ring.DropOldest(_target); // Aufstart: Ueberhang abschneiden (kein Ueberlauf)
            _resampler.Reset();
            _ctlFresh = true;
            _fadeIn = FadeFrames;
            _baseValid = false;
            Volatile.Write(ref _running, true);
            lag = _ring.Lag;
        }

        Measure();
        double ratio = _nominal * (1.0 + _c);

        int need = (int)(n * ratio) + (int)Math.Ceiling(ratio) + 4; // obere Schranke des Verbrauchs
        if (need > _scratchFrames) need = _scratchFrames;
        int take = (int)Math.Min(lag, need);

        if (!_ring.TryPeek(_scratch, take))
        {
            // Der Produzent hat uns ueberrundet oder waehrend des Kopierens ueberschrieben: neu aufsetzen.
            long dropped = _ring.DropOldest(_target);
            Interlocked.Increment(ref _overruns);
            Interlocked.Add(ref _droppedFrames, dropped);
            Silence(dst, off, n);
            Volatile.Write(ref _running, false);
            return;
        }

        int produced = _resampler.Process(_scratch, take, ratio, dst, off, n, out int consumed);
        _ring.Advance(consumed);

        if (produced < n)
        {
            // Unterlauf: Signalende kurz ausblenden, Rest Stille, danach neu fuellen lassen.
            FadeOutTail(dst, off, produced);
            ClearRange(dst, off + produced, n - produced);
            Volatile.Write(ref _running, false);
            if (_countUnderruns) Interlocked.Increment(ref _underruns);
            return;
        }

        if (_fadeIn > 0) ApplyFadeIn(dst, off, n);
    }

    /// <summary>Aktualisiert den mittleren Fuellstand aus den Zeitintegralen von Schreib- und Leseposition und stellt den Regler nach.</summary>
    private void Measure()
    {
        long now = _clock();
        if (!_ring.TryReadWriteIntegral(now, out Int128 jw, out long t)) return;

        if (!_baseValid)
        {
            _jwPrev = jw;
            _jrPrev = Int128.Zero;
            _tPrev = t;
            _rAfter = _ring.TotalRead;
            _accJ = Int128.Zero;
            _accT = 0;
            _baseValid = true;
            return;
        }

        long dt = t - _tPrev;
        if (dt <= 0) return;
        // Leseposition war seit dem letzten Block konstant (_rAfter): Integral fortschreiben
        Int128 jr = _jrPrev + (Int128)_rAfter * dt;
        _accJ += (jw - _jwPrev) - (jr - _jrPrev); // Integral von (W - R) ueber das Fenster
        _accT += dt;
        _jwPrev = jw;
        _jrPrev = jr;
        _tPrev = t;

        if (_ctlFresh)
        {
            // Anlauf: erst 250 ms messen, dann auf dem gemessenen Mittelwert starten (kein Anfangsversatz, kein Tonhoehen-Ausschlag)
            if (_accT < _warmupTicks) return;
            double first = (double)_accJ / _accT;
            _controller.Start(first);
            Volatile.Write(ref _fill, first);
            _ctlFresh = false;
            _accJ = Int128.Zero;
            _accT = 0;
            return;
        }

        if (_accT >= _minWindowTicks)
        {
            double mean = (double)_accJ / _accT; // mittlerer Fuellstand im Fenster (Frames)
            _c = _controller.Update(mean, _accT / _ticksPerSecond);
            Volatile.Write(ref _ppm, _c * 1e6);
            Volatile.Write(ref _fill, _controller.FilteredFill);
            _accJ = Int128.Zero;
            _accT = 0;
        }
    }

    private void Silence(float[][] dst, int off, int n) => ClearRange(dst, off, n);

    private void ClearRange(float[][] dst, int off, int n)
    {
        if (n <= 0) return;
        int ch = _ring.Channels;
        for (int s = 0; s < ch; s++) Array.Clear(dst[s], off, n);
    }

    private void FadeOutTail(float[][] dst, int off, int produced)
    {
        int k = Math.Min(produced, FadeFrames);
        if (k <= 0) return;
        int ch = _ring.Channels;
        int start = off + produced - k;
        for (int s = 0; s < ch; s++)
        {
            float[] a = dst[s];
            for (int i = 0; i < k; i++) a[start + i] *= (k - 1 - i) / (float)k;
        }
    }

    private void ApplyFadeIn(float[][] dst, int off, int n)
    {
        int k = Math.Min(_fadeIn, n);
        int pos0 = FadeFrames - _fadeIn;
        int ch = _ring.Channels;
        for (int s = 0; s < ch; s++)
        {
            float[] a = dst[s];
            for (int i = 0; i < k; i++) a[off + i] *= (pos0 + i) / (float)FadeFrames;
        }
        _fadeIn -= k;
    }
}
