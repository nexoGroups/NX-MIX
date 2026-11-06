using System.Runtime.Intrinsics;

namespace NxMix.Engine;

/// <summary>
/// NX-MIX Mixer-Kern. Signalweg je Block:
///  Eingang (Rohpegel messen) -> Delay -> Filterkette -> [Gain*Mute/Solo*Phase*Matrix*Master, zu einer Kreuzungspunkt-Verstaerkung verschmolzen]
///  -> Ausgang: Filterkette -> Delay -> Gain/Mute/Solo/Phase -> Limiter (~1 ms Lookahead, Brickwall) -> Pegelmessung.
/// Process() laeuft auf genau einem Audio-Thread, allokationsfrei und sperrfrei. TrySetSettings darf aus jedem Thread kommen:
/// es baut einen fertigen Plan (Compiled) und veroeffentlicht ihn per Volatile; der Audio-Thread uebernimmt ihn am Blockanfang
/// (Filter-/Delay-/Limiterzustaende bleiben bei gleicher Struktur erhalten, alle Verstaerkungen und Koeffizienten laufen ueber ~10 ms als Rampe).
/// </summary>
public sealed class MixerEngine : IMixerEngine
{
    /// <summary>Laengere Bloecke werden intern in Teilbloecke dieser Groesse zerlegt.</summary>
    public const int MaxChunk = 2048;
    /// <summary>Maximale Kanalverzoegerung (darueber wird geklemmt).</summary>
    public const double MaxDelayMs = 1000.0;
    /// <summary>Dauer der Rampen bei Einstellungswechseln.</summary>
    public const double RampMs = 10.0;

    const double MaxMatrixDb = 24.0;
    const double MaxMasterDb = 12.0;
    const double HardMaxGainDb = 60.0;

    readonly int _nIn, _nOut, _sr, _rampLen;
    readonly ChannelRt[] _inRt, _outRt;
    readonly float[][] _inBuf, _mixBuf;
    readonly double[] _dbl = new double[MaxChunk];
    readonly float[] _rampIdx = new float[MaxChunk];
    readonly float[] _dummyOut = new float[MaxChunk];
    readonly float[] _xfBuf = new float[MaxChunk];
    readonly float[] _zeroBuf = new float[MaxChunk];
    readonly Vector256<double>[] _d4 = Biquad4.Supported ? new Vector256<double>[MaxChunk] : Array.Empty<Vector256<double>>();
    readonly ChannelRt[] _laneRt = new ChannelRt[4];
    readonly float[][] _laneBuf = new float[4][];
    readonly int[] _simdList;
    readonly float[] _crossCur;
    readonly bool[] _used;
    readonly int[] _inPeaks, _outPeaks;

    readonly object _setLock = new();
    MixerSettings _settings;
    Compiled? _latest;
    Compiled _cur;
    int _rampLeft;
    long _hits;
    SpectrumCapture? _spectrum;

    public int InputChannels => _nIn;
    public int OutputChannels => _nOut;
    public int SampleRate => _sr;
    public MixerSettings Settings => Volatile.Read(ref _settings);
    public long LimiterHits => Interlocked.Read(ref _hits);

    public SpectrumCapture StartSpectrum(bool input, int channel)
    {
        if ((uint)channel >= (uint)(input ? _nIn : _nOut)) throw new ArgumentOutOfRangeException(nameof(channel));
        var capture = new SpectrumCapture(input, channel, _sr);
        Volatile.Write(ref _spectrum, capture);
        return capture;
    }

    public void StopSpectrum(SpectrumCapture capture) => Interlocked.CompareExchange(ref _spectrum, null, capture);

    public MixerEngine(MixerSettings initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        if (initial.SampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(initial), "SampleRate muss > 0 sein.");
        _sr = initial.SampleRate;
        _nIn = initial.Inputs?.Count ?? 0;
        _nOut = initial.Outputs?.Count ?? 0;
        _rampLen = Math.Max(1, (int)Math.Round(_sr * RampMs / 1000.0));

        int maxDelay = (int)Math.Ceiling(MaxDelayMs * _sr / 1000.0);
        _inRt = new ChannelRt[_nIn];
        _outRt = new ChannelRt[_nOut];
        _inBuf = new float[_nIn][];
        _mixBuf = new float[_nOut][];
        for (int i = 0; i < _nIn; i++)
        {
            _inRt[i] = new ChannelRt(new DelayLine(maxDelay, MaxChunk), null);
            _inBuf[i] = new float[MaxChunk];
        }
        for (int o = 0; o < _nOut; o++)
        {
            _outRt[o] = new ChannelRt(new DelayLine(maxDelay, MaxChunk), new Limiter(_sr));
            _mixBuf[o] = new float[MaxChunk];
        }
        for (int j = 0; j < MaxChunk; j++) _rampIdx[j] = j + 1;
        _used = new bool[_nIn];
        _simdList = new int[Math.Max(_nIn, _nOut)];
        _inPeaks = new int[_nIn];
        _outPeaks = new int[_nOut];

        var c = Compile(initial);
        _crossCur = (float[])c.Cross.Clone();
        for (int i = 0; i < _nIn; i++)
        {
            _inRt[i].Sections = c.InSections[i];
            _inRt[i].Delay.Snap(c.InDelay[i]);
        }
        for (int o = 0; o < _nOut; o++)
        {
            var rt = _outRt[o];
            rt.Sections = c.OutSections[o];
            rt.Delay.Snap(c.OutDelay[o]);
            rt.GainCur = c.OutGain[o];
            rt.Lim!.Ceiling = c.Ceiling[o];
        }
        _cur = c;
        _settings = initial;
    }

    // ------------------------------------------------------------------ Einstellungen

    public bool TrySetSettings(MixerSettings settings)
    {
        if (settings is null) return false;
        if (settings.SampleRate != _sr) return false;
        if (settings.Inputs is null || settings.Outputs is null) return false;
        if (settings.Inputs.Count != _nIn || settings.Outputs.Count != _nOut) return false;
        lock (_setLock)
        {
            var c = Compile(settings);
            Volatile.Write(ref _latest, c);
            Volatile.Write(ref _settings, settings);
        }
        return true;
    }

    static float ChannelGain(ChannelDsp? ch, bool anySolo)
    {
        if (ch is null) return 1f;
        if (ch.Mute || (anySolo && !ch.Solo)) return 0f;
        double maxG = double.IsNaN(ch.MaxGainDb) ? 12.0 : ch.MaxGainDb;
        double g = double.IsNaN(ch.GainDb) ? double.NegativeInfinity : Math.Min(ch.GainDb, maxG);
        g = Math.Min(g, HardMaxGainDb);
        double lin = Dsp.DbToLin(g);
        if (lin < 1e-9) return 0f;
        return (float)(ch.InvertPhase ? -lin : lin);
    }

    Section[] BuildSections(ChannelDsp? channel, bool input, List<BiquadCoefficients> coef, List<int> kinds)
    {
        coef.Clear();
        kinds.Clear();
        FilterDesign.DesignChannel(channel, _sr, input, coef, kinds);
        var arr = new Section[coef.Count];
        for (int s = 0; s < arr.Length; s++) arr[s] = new Section(kinds[s], coef[s]);
        return arr;
    }

    double DelaySamples(ChannelDsp? ch)
    {
        if (ch is null || double.IsNaN(ch.DelayMs) || ch.DelayMs <= 0) return 0;
        return Math.Min(ch.DelayMs, MaxDelayMs) * _sr / 1000.0;
    }

    Compiled Compile(MixerSettings s)
    {
        var c = new Compiled { Settings = s };
        var ins = s.Inputs;
        var outs = s.Outputs;

        bool soloIn = false, soloOut = false;
        for (int i = 0; i < _nIn; i++) if (ins[i]?.Solo == true) soloIn = true;
        for (int o = 0; o < _nOut; o++) if (outs[o]?.Solo == true) soloOut = true;

        double master = double.IsNaN(s.MasterGainDb) ? 0.0 : Dsp.DbToLin(Math.Min(s.MasterGainDb, MaxMasterDb));

        var inGain = new float[_nIn];
        for (int i = 0; i < _nIn; i++) inGain[i] = ChannelGain(ins[i], soloIn);

        c.Cross = new float[_nOut * _nIn];
        var m = s.MatrixDb;
        for (int o = 0; o < _nOut; o++)
        {
            for (int i = 0; i < _nIn; i++)
            {
                double db = double.NegativeInfinity;
                if (m != null && i < m.Length && m[i] != null && o < m[i].Length) db = m[i][o];
                double lin = Dsp.DbToLin(Math.Min(db, MaxMatrixDb));    // NaN und -inf -> 0
                double v = inGain[i] * master * lin;
                if (Math.Abs(v) < 1e-9) v = 0;
                c.Cross[o * _nIn + i] = (float)v;
            }
        }

        var coef = new List<BiquadCoefficients>(32);
        var kinds = new List<int>(32);

        c.InSections = new Section[_nIn][];
        c.InDelay = new double[_nIn];
        for (int i = 0; i < _nIn; i++)
        {
            c.InSections[i] = BuildSections(ins[i], true, coef, kinds);
            c.InDelay[i] = DelaySamples(ins[i]);
        }

        c.OutSections = new Section[_nOut][];
        c.OutDelay = new double[_nOut];
        c.OutGain = new float[_nOut];
        c.Ceiling = new float[_nOut];
        for (int o = 0; o < _nOut; o++)
        {
            c.OutSections[o] = BuildSections(outs[o], false, coef, kinds);
            c.OutDelay[o] = DelaySamples(outs[o]);
            c.OutGain[o] = ChannelGain(outs[o], soloOut);
            c.Ceiling[o] = Limiter.CeilingToLinear(outs[o]?.CeilingDbfs ?? -1.0);
        }
        return c;
    }

    // ------------------------------------------------------------------ Pegel

    public void ReadInputPeaks(Span<float> destination) => ReadPeaks(_inPeaks, destination);
    public void ReadOutputPeaks(Span<float> destination) => ReadPeaks(_outPeaks, destination);

    static void ReadPeaks(int[] peaks, Span<float> destination)
    {
        int n = Math.Min(peaks.Length, destination.Length);
        for (int i = 0; i < n; i++)
            destination[i] = BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref peaks[i], 0));
        for (int i = n; i < destination.Length; i++) destination[i] = 0f;
    }

    static void UpdatePeak(int[] peaks, int index, float peak)
    {
        int bits = BitConverter.SingleToInt32Bits(peak);   // Betrag >= 0: Bitmuster ist monoton
        ref int slot = ref peaks[index];
        int old = Volatile.Read(ref slot);
        while (bits > old)
        {
            int prev = Interlocked.CompareExchange(ref slot, bits, old);
            if (prev == old) break;
            old = prev;
        }
    }

    // ------------------------------------------------------------------ Audio

    /// <summary>Nur vom Audio-Thread rufen. inputs[InputChannels][>=frames], outputs[OutputChannels][>=frames].</summary>
    public void Process(float[][] inputs, float[][] outputs, int frames)
    {
        if (frames <= 0) return;
        var latest = Volatile.Read(ref _latest);
        if (latest != null && !ReferenceEquals(latest, _cur)) Adopt(latest);

        int pos = 0;
        while (pos < frames)
        {
            int n = Math.Min(MaxChunk, frames - pos);
            ProcessChunk(inputs, outputs, pos, n);
            pos += n;
        }
    }

    void Adopt(Compiled next)
    {
        for (int i = 0; i < _nIn; i++) Transfer(_inRt[i], next.InSections[i]);
        for (int o = 0; o < _nOut; o++) Transfer(_outRt[o], next.OutSections[o]);
        _cur = next;
        _rampLeft = _rampLen;
    }

    void Transfer(ChannelRt rt, Section[] ns)
    {
        var os = rt.Sections;
        bool same = os.Length == ns.Length;
        for (int k = 0; same && k < os.Length; k++) same = os[k].Kind == ns[k].Kind;
        if (same)
        {
            for (int k = 0; k < os.Length; k++)
            {
                var o = os[k];
                var n = ns[k];
                n.S1 = o.S1; n.S2 = o.S2;
                n.B0 = o.B0; n.C1 = o.C1; n.C2 = o.C2; n.A1 = o.A1; n.A2 = o.A2;   // Rampe startet beim aktuellen Wert
            }
        }
        else if (os.Length > 0 || ns.Length > 0)
        {
            // Struktur geaendert: alte Kette laeuft ~10 ms weiter und wird ueberblendet (laeuft eine Ueberblendung noch, wird sie ersetzt)
            rt.OldSections = os;
            rt.XfadeLeft = _rampLen;
        }
        rt.Sections = ns;
    }

    void ProcessChunk(float[][] inputs, float[][] outputs, int pos, int n)
    {
        var plan = _cur;
        int nIn = _nIn, nOut = _nOut;
        int rampBefore = _rampLeft;
        int k = rampBefore < n ? rampBefore : n;       // Anzahl Rampen-Samples in diesem Teilblock
        var cross = _crossCur;
        var tgt = plan.Cross;
        var used = _used;
        var spectrum = Volatile.Read(ref _spectrum);
        int spectrumInput = spectrum is { Input: true } ? spectrum.Channel : -1;
        int spectrumOutput = spectrum is { Input: false } ? spectrum.Channel : -1;

        // welche Eingaenge werden ueberhaupt gebraucht?
        for (int i = 0; i < nIn; i++) used[i] = false;
        for (int o = 0; o < nOut; o++)
        {
            int b = o * nIn;
            for (int i = 0; i < nIn; i++)
                if (cross[b + i] != 0f || tgt[b + i] != 0f) used[i] = true;
        }

        // ---- Eingaenge: Rohpegel, Delay, Filterkette
        int simdCnt = 0;
        for (int i = 0; i < nIn; i++)
        {
            var buf = _inBuf[i];
            float[]? src = inputs != null && i < inputs.Length ? inputs[i] : null;
            float peak;
            if (src != null && src.Length >= pos + n) peak = Dsp.SanitizeCopy(src, pos, buf, n, Dsp.HugeIn);
            else { Array.Clear(buf, 0, n); peak = 0f; }
            UpdatePeak(_inPeaks, i, peak);

            var rt = _inRt[i];
            rt.Delay.Push(buf, n);                 // Ring laeuft immer mit (Verzoegerung bleibt beim Wieder-Einschalten gueltig)
            if (!used[i] && i != spectrumInput)
            {
                rt.Stale = true;
                var secs = rt.Sections;
                for (int s = 0; s < secs.Length; s++) { secs[s].ResetState(); secs[s].SnapToTarget(); }
                rt.XfadeLeft = 0;
                rt.OldSections = Array.Empty<Section>();
                continue;
            }
            rt.Stale = false;
            rt.Delay.Read(buf, n, plan.InDelay[i], _rampLen);
            if (i == spectrumInput) spectrum!.Before(buf.AsSpan(0, n));
            if (SimdEligible(rt, k)) _simdList[simdCnt++] = i;
            else RunChain(rt, buf, n, k, rampBefore);
        }
        RunSimdGroups(_inRt, _inBuf, simdCnt, n);
        if (spectrumInput >= 0) spectrum!.After(_inBuf[spectrumInput].AsSpan(0, n));

        // ---- Matrix: mix[o] = sum_i cross[o][i] * in[i]
        for (int o = 0; o < nOut; o++)
        {
            var mix = _mixBuf[o];
            bool first = true;
            int b = o * nIn;
            for (int i = 0; i < nIn; i++)
            {
                float cur = cross[b + i], t = tgt[b + i];
                if (cur == 0f && t == 0f) continue;
                var x = _inBuf[i];
                if (cur == t || k == 0)
                {
                    Dsp.MulConst(mix, x, 0, n, t, first);
                }
                else
                {
                    float step = (t - cur) / rampBefore;
                    Dsp.MulRamp(mix, x, _rampIdx, k, cur, step, first);
                    if (k < n) Dsp.MulConst(mix, x, k, n, t, first);
                }
                first = false;
            }
            if (first) Array.Clear(mix, 0, n);
        }
        if (k > 0)
        {
            for (int idx = 0; idx < cross.Length; idx++)
            {
                float cur = cross[idx], t = tgt[idx];
                if (cur != t) cross[idx] = k == rampBefore ? t : cur + (t - cur) / rampBefore * k;
            }
        }

        // ---- Ausgaenge: Filterkette
        if (spectrumOutput >= 0) spectrum!.Before(_mixBuf[spectrumOutput].AsSpan(0, n));
        simdCnt = 0;
        for (int o = 0; o < nOut; o++)
        {
            var rt = _outRt[o];
            if (SimdEligible(rt, k)) _simdList[simdCnt++] = o;
            else RunChain(rt, _mixBuf[o], n, k, rampBefore);
        }
        RunSimdGroups(_outRt, _mixBuf, simdCnt, n);
        if (spectrumOutput >= 0) spectrum!.After(_mixBuf[spectrumOutput].AsSpan(0, n));

        // ---- Ausgaenge: Delay, Gain, Limiter, Pegel
        for (int o = 0; o < nOut; o++)
        {
            var rt = _outRt[o];
            var mix = _mixBuf[o];

            rt.Delay.Push(mix, n);
            rt.Delay.Read(mix, n, plan.OutDelay[o], _rampLen);

            float gt = plan.OutGain[o], gc = rt.GainCur;
            if (gc == gt || k == 0)
            {
                if (gt != 1f) Dsp.Scale(mix, 0, n, gt);
                rt.GainCur = gt;
            }
            else
            {
                float step = (gt - gc) / rampBefore;
                Dsp.ScaleRamp(mix, _rampIdx, k, gc, step);
                if (k < n) Dsp.Scale(mix, k, n, gt);
                rt.GainCur = k == rampBefore ? gt : gc + step * k;
            }

            float pk = Dsp.SanitizeInPlace(mix, n, Dsp.HugeOut, out _);

            float[]? d = outputs != null && o < outputs.Length ? outputs[o] : null;
            float[] dst;
            int dOff;
            if (d != null && d.Length >= pos + n) { dst = d; dOff = pos; }
            else { dst = _dummyOut; dOff = 0; }

            var lim = rt.Lim!;
            lim.Ceiling = plan.Ceiling[o];
            if (lim.Process(mix, dst, dOff, n, pk)) Interlocked.Increment(ref _hits);

            UpdatePeak(_outPeaks, o, Dsp.Peak(dst, dOff, n));
        }

        if (k > 0) _rampLeft = rampBefore - k;
    }

    /// <summary>
    /// Filterkette eines Kanals in place. Nach einem Strukturwechsel (andere Filter/Reihenfolge) laeuft die alte Kette
    /// ~10 ms weiter und wird auf die neue Kette ueberblendet - kein Sprung, kein Knacken.
    /// </summary>
    void RunChain(ChannelRt rt, float[] x, int n, int k, int rampBefore)
    {
        int xl = rt.XfadeLeft;
        if (xl <= 0)
        {
            RunSections(rt.Sections, x, n, k, rampBefore);
            return;
        }

        var old = _xfBuf;
        Array.Copy(x, old, n);
        RunSections(rt.OldSections, old, n, 0, 0);
        RunSections(rt.Sections, x, n, k, rampBefore);

        int m = Math.Min(n, xl);
        int r = _rampLen;
        float inv = 1f / r;
        int done = r - xl;
        for (int j = 0; j < m; j++)
        {
            float a = (done + j + 1) * inv;
            x[j] = old[j] + (x[j] - old[j]) * a;
        }
        rt.XfadeLeft = xl - m;
        if (rt.XfadeLeft == 0) rt.OldSections = Array.Empty<Section>();
    }

    /// <summary>Eine Sektionskette in place (float -> double -> float). k/rampBefore: Koeffizientenrampe (0 = keine).</summary>
    void RunSections(Section[] secs, float[] x, int n, int k, int rampBefore)
    {
        int count = secs.Length;
        if (count == 0) return;

        var d = _dbl;
        for (int j = 0; j < n; j++) d[j] = x[j];

        int s = 0;
        while (s < count)
        {
            var a = secs[s];
            if (s + 1 < count)
            {
                var b = secs[s + 1];
                if ((k == 0 || !a.NeedsRamp) && (k == 0 || !b.NeedsRamp))
                {
                    Biquad.RunPair(d, 0, n, a, b);
                    s += 2;
                    continue;
                }
            }
            Biquad.Run(d, n, a, k, rampBefore);
            s++;
        }

        for (int j = 0; j < n; j++) x[j] = (float)d[j];

        FinishSections(secs, x, n);
    }

    /// <summary>Nach der Kette: NaN/Inf/Riesiges ersetzen (vergiftete Zustaende verwerfen), sonst Denormals aus den Zustaenden fegen.</summary>
    static void FinishSections(Section[] secs, float[] x, int n)
    {
        Dsp.SanitizeInPlace(x, n, Dsp.HugeOut, out bool bad);
        if (bad)
        {
            for (int q = 0; q < secs.Length; q++) secs[q].ResetState();
        }
        else
        {
            for (int q = 0; q < secs.Length; q++) secs[q].FlushDenormals();
        }
    }

    // ------------------------------------------------------------------ SIMD-Pfad (4 Kanaele je AVX2-Vektor)

    /// <summary>Beharrungszustand: keine Kreuzblende, keine laufende Koeffizientenrampe, mindestens eine Sektion.</summary>
    static bool SimdEligible(ChannelRt rt, int k)
    {
        if (!Biquad4.Supported || rt.XfadeLeft != 0) return false;
        var secs = rt.Sections;
        if (secs.Length == 0) return false;
        if (k > 0)
            for (int s = 0; s < secs.Length; s++)
                if (secs[s].NeedsRamp) return false;
        return true;
    }

    /// <summary>Verarbeitet die Kanaele rts[list[0..cnt)] (Puffer bufs[..], je n Samples) in Vierergruppen.</summary>
    void RunSimdGroups(ChannelRt[] rts, float[][] bufs, int cnt, int n)
    {
        if (cnt == 0) return;
        var d4 = _d4;
        for (int g = 0; g < cnt; g += 4)
        {
            int m = Math.Min(4, cnt - g);
            int maxS = 0;
            for (int l = 0; l < 4; l++)
            {
                if (l < m)
                {
                    int ch = _simdList[g + l];
                    _laneRt[l] = rts[ch];
                    _laneBuf[l] = bufs[ch];
                    maxS = Math.Max(maxS, rts[ch].Sections.Length);
                }
                else
                {
                    _laneBuf[l] = _zeroBuf;
                }
            }
            float[] b0 = _laneBuf[0], b1 = _laneBuf[1], b2 = _laneBuf[2], b3 = _laneBuf[3];

            for (int j = 0; j < n; j++)
                d4[j] = Vector256.Create((double)b0[j], b1[j], b2[j], b3[j]);

            Biquad4.Run(d4, n, _laneRt, m, maxS);

            float[] o1 = m > 1 ? b1 : _dummyOut, o2 = m > 2 ? b2 : _dummyOut, o3 = m > 3 ? b3 : _dummyOut;
            for (int j = 0; j < n; j++)
            {
                var f = System.Runtime.Intrinsics.X86.Avx.ConvertToVector128Single(d4[j]);
                b0[j] = f.GetElement(0);
                o1[j] = f.GetElement(1);
                o2[j] = f.GetElement(2);
                o3[j] = f.GetElement(3);
            }

            for (int l = 0; l < m; l++) FinishSections(_laneRt[l].Sections, _laneBuf[l], n);
        }
    }
}
