using System.Runtime.Intrinsics;

namespace NxMix.Engine;

/// <summary>
/// NX-MIX Echtzeit-Mischkern.
/// Signalweg je Teilblock:
///   1. Eingang: Sanitize &amp; Rohpegel -> Eingangs-Delay -> Eingangs-Filterkette
///   2. Routing:
///      a) Direkte Matrix (Inputs -> Outputs, inkl. Eingangs-Gain, Trim, Eingangs-Device-Master und Pan)
///      b) Echte Mix-Busse in topologischer Reihenfolge (Inputs/Vor-Busse -> Bus-Summenpuffer -> Bus-Gain/Mute/Solo
///         -> echtes Post-Fader-Bus-Metering -> Ziel-Outputs)
///   3. Ausgang: Ausgangs-Filterkette (Crossover/PEQ + REW-Korrektur mit Preamp) -> Ausgangs-Delay
///      -> Ausgangs-Kanal-Fader * Ausgangs-Device-Master * Global-Master -> Brickwall-Limiter (~1 ms Lookahead)
///      -> Ausgangs-Kanal- &amp; Device-Master-Pegelmessung.
///
/// Process() laeuft auf genau einem Audio-Thread, allokationsfrei (0 Bytes Steady-State) und sperrfrei.
/// Im gepoolten Modus (CreatePooled, Standard 128 In x 64 Bus x 128 Out) koennen Kanaele, Geraete und Busse
/// im laufenden Betrieb bis zur Kapazitaetsgrenze ohne Stream-Neustart hinzugefuegt oder geaendert werden.
/// </summary>
public sealed class MixerEngine : IMixerEngine, IInputMonitorBlockProcessor
{
    /// <summary>Laengere Bloecke werden intern in Teilbloecke dieser Groesse zerlegt.</summary>
    public const int MaxChunk = 2048;
    /// <summary>Maximale Kanalverzoegerung (darueber wird geklemmt).</summary>
    public const double MaxDelayMs = 1000.0;
    /// <summary>Standard-Dauer der Klickschutzrampen bei Einstellungswechseln.</summary>
    public const double RampMs = 10.0;
    /// <summary>Maximale Dauer einer Engine-seitigen Snapshot-Ueberblendung (10 Sekunden).</summary>
    public const double MaxTransitionMs = 10_000.0;

    public const int DefaultMaxInputs = 128;
    public const int DefaultMaxBusChannels = 64;
    public const int DefaultMaxOutputs = 128;
    public const int MaxDeviceMasters = 64;

    const double MaxMatrixDb = 24.0;
    const double MaxMasterDb = 12.0;
    const double HardMaxGainDb = 60.0;

    readonly int _capIn, _capOut, _capBus, _sr, _defaultRampLen;
    readonly bool _pooled;
    readonly ChannelRt[] _inRt, _outRt;
    readonly float[][] _inBuf, _mixBuf, _busBuf;
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
    readonly float[] _inToBusCur, _busToBusCur, _busGainCur, _busToOutCur;
    readonly bool[] _used;
    readonly int[] _inPeaks, _outPeaks, _busSlotPeaks, _devPeaks;

    readonly object _setLock = new();
    MixerSettings _settings;
    Compiled? _latest;
    Compiled _cur;
    int _rampLeft;
    bool _busActiveInPrev;
    long _hits;
    SpectrumCapture? _spectrum;

    public int InputChannels => _capIn;
    public int OutputChannels => _capOut;
    public int MaxBusChannels => _capBus;
    public bool IsPooled => _pooled;
    public int SampleRate => _sr;
    public MixerSettings Settings => Volatile.Read(ref _settings);
    public long LimiterHits => Interlocked.Read(ref _hits);

    /// <summary>Erzeugt eine vorallokierte Live-Engine mit fester Slot-Kapazitaet (Standard: 128 In x 64 Bus x 128 Out),
    /// sodass Geraete-, Kanal-, VAIO- und Bus-Aenderungen ohne Audio-Unterbrechung ueber TrySetSettings laufen.</summary>
    public static MixerEngine CreatePooled(
        MixerSettings initial,
        int maxInputs = DefaultMaxInputs,
        int maxBusChannels = DefaultMaxBusChannels,
        int maxOutputs = DefaultMaxOutputs)
    {
        ArgumentNullException.ThrowIfNull(initial);
        int cIn = Math.Max(maxInputs, initial.Inputs?.Count ?? 0);
        int cOut = Math.Max(maxOutputs, initial.Outputs?.Count ?? 0);
        int cBus = Math.Max(maxBusChannels, initial.Buses?.Sum(b => Math.Clamp(b.Channels, 1, 64)) ?? 0);
        return new MixerEngine(initial, cIn, cOut, cBus, pooled: true);
    }

    public MixerEngine(MixerSettings initial)
        : this(initial, initial?.Inputs?.Count ?? 0, initial?.Outputs?.Count ?? 0, DefaultMaxBusChannels, pooled: false)
    {
    }

    private MixerEngine(MixerSettings initial, int capIn, int capOut, int capBus, bool pooled)
    {
        ArgumentNullException.ThrowIfNull(initial);
        if (initial.SampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(initial), "SampleRate muss > 0 sein.");
        _sr = initial.SampleRate;
        _capIn = Math.Max(0, capIn);
        _capOut = Math.Max(0, capOut);
        _capBus = Math.Max(0, capBus);
        _pooled = pooled;
        _defaultRampLen = Math.Max(1, (int)Math.Round(_sr * RampMs / 1000.0));

        int maxDelay = (int)Math.Ceiling(MaxDelayMs * _sr / 1000.0);
        _inRt = new ChannelRt[_capIn];
        _outRt = new ChannelRt[_capOut];
        _inBuf = new float[_capIn][];
        _mixBuf = new float[_capOut][];
        _busBuf = new float[_capBus][];

        for (int i = 0; i < _capIn; i++)
        {
            _inRt[i] = new ChannelRt(new DelayLine(maxDelay, MaxChunk), null);
            _inBuf[i] = new float[MaxChunk];
        }
        for (int o = 0; o < _capOut; o++)
        {
            _outRt[o] = new ChannelRt(new DelayLine(maxDelay, MaxChunk), new Limiter(_sr));
            _mixBuf[o] = new float[MaxChunk];
        }
        for (int b = 0; b < _capBus; b++)
        {
            _busBuf[b] = new float[MaxChunk];
        }
        for (int j = 0; j < MaxChunk; j++) _rampIdx[j] = j + 1;

        _used = new bool[_capIn];
        _simdList = new int[Math.Max(1, Math.Max(_capIn, _capOut))];
        _inPeaks = new int[_capIn];
        _outPeaks = new int[_capOut];
        _busSlotPeaks = new int[_capBus];
        _devPeaks = new int[MaxDeviceMasters];

        var c = Compile(initial);
        _crossCur = (float[])c.Cross.Clone();
        _inToBusCur = new float[_capBus * _capIn];
        _busToBusCur = new float[_capBus * _capBus];
        _busGainCur = new float[_capBus];
        _busToOutCur = new float[_capOut * _capBus];
        if (c.BusPlan is { } bp)
        {
            Array.Copy(bp.InToBus, _inToBusCur, _inToBusCur.Length);
            Array.Copy(bp.BusToBus, _busToBusCur, _busToBusCur.Length);
            Array.Copy(bp.SlotGain, _busGainCur, _busGainCur.Length);
            Array.Copy(bp.BusToOut, _busToOutCur, _busToOutCur.Length);
            _busActiveInPrev = bp.TotalSlots > 0;
        }

        for (int i = 0; i < _capIn; i++)
        {
            _inRt[i].Sections = c.InSections[i];
            _inRt[i].Delay.Snap(c.InDelay[i]);
        }
        for (int o = 0; o < _capOut; o++)
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

    sealed class OutputTestTone(int output)
    {
        public readonly int Output = output;
        public int Position;
    }

    OutputTestTone? _outputTestTone;

    public bool TryStartOutputTestTone(int output) =>
        (uint)output < (uint)_capOut &&
        Interlocked.CompareExchange(ref _outputTestTone, new OutputTestTone(output), null) == null;

    public void CancelOutputTestTone() => Interlocked.Exchange(ref _outputTestTone, null);

    void AddOutputTestTone(OutputTestTone tone, float[] mix, int count, float peakGain)
    {
        const float amplitude = 0.01f; // −40 dBFS maximum before the limiter.
        int length = _sr / 2;
        int fade = Math.Max(1, _sr / 50);
        int end = Math.Min(length, tone.Position + count);
        float scale = amplitude / Math.Max(1f, peakGain);
        double phasePerSample = 2 * Math.PI * 440 / _sr;
        for (int i = 0; tone.Position < end; i++, tone.Position++)
        {
            float envelope = Math.Min(1f, Math.Min(tone.Position, length - 1 - tone.Position) / (float)fade);
            mix[i] += scale * envelope * (float)Math.Sin(tone.Position * phasePerSample);
        }
        if (tone.Position == length) Interlocked.CompareExchange(ref _outputTestTone, null, tone);
    }

    public SpectrumCapture StartSpectrum(bool input, int channel, int windowSize = 0)
    {
        if ((uint)channel >= (uint)(input ? _capIn : _capOut)) throw new ArgumentOutOfRangeException(nameof(channel));
        var capture = new SpectrumCapture(input, channel, _sr, windowSize);
        Volatile.Write(ref _spectrum, capture);
        return capture;
    }

    public void StopSpectrum(SpectrumCapture capture) => Interlocked.CompareExchange(ref _spectrum, null, capture);

    // ------------------------------------------------------------------ Einstellungen

    public bool TrySetSettings(MixerSettings settings)
    {
        if (settings is null) return false;
        if (settings.SampleRate != _sr) return false;
        if (settings.Inputs is null || settings.Outputs is null) return false;
        if (_pooled)
        {
            if (settings.Inputs.Count > _capIn || settings.Outputs.Count > _capOut) return false;
            int busSlots = 0;
            for (int i = 0; i < settings.Buses.Count; i++) busSlots += settings.Buses[i].Channels;
            if (busSlots > _capBus) return false;
        }
        else
        {
            if (settings.Inputs.Count != _capIn || settings.Outputs.Count != _capOut) return false;
        }

        lock (_setLock)
        {
            Compiled c;
            try { c = Compile(settings); }
            catch (ArgumentException) { return false; }
            Volatile.Write(ref _latest, c);
            Volatile.Write(ref _settings, settings);
        }
        return true;
    }

    static float ChannelGain(ChannelDsp? ch, bool anySolo, bool includeTrim)
    {
        if (ch is null) return 0f;
        if (ch.Mute || (anySolo && !ch.Solo)) return 0f;
        double maxG = double.IsNaN(ch.MaxGainDb) ? 12.0 : ch.MaxGainDb;
        double g = double.IsNaN(ch.GainDb) ? double.NegativeInfinity : Math.Min(ch.GainDb, maxG);
        if (includeTrim && double.IsFinite(ch.TrimDb)) g += Math.Clamp(ch.TrimDb, -48.0, 48.0);
        g = Math.Min(g, HardMaxGainDb);
        double lin = Dsp.DbToLin(g);
        if (lin < 1e-9) return 0f;
        return (float)(ch.InvertPhase ? -lin : lin);
    }

    static double DeviceMasterLinear(DeviceMasterDsp? dm)
    {
        if (dm is null) return 1.0;
        if (dm.Mute) return 0.0;
        double db = double.IsNaN(dm.GainDb) ? 0.0 : Math.Clamp(dm.GainDb, -120.0, MaxMasterDb);
        if (dm.Dim && double.IsFinite(dm.DimDb)) db += Math.Clamp(dm.DimDb, -60.0, 0.0);
        double lin = Dsp.DbToLin(db);
        return lin < 1e-9 ? 0.0 : lin;
    }

    /// <summary>Constant-Power-Panning (-3 dB in der Mitte, 0 dB an den Anschlaegen -> normiert auf 1.0 bei Pan = 0, damit neutrale Signalwege bitgenau 0 dB bleiben).</summary>
    static double PanMultiplier(double pan, int targetChannelIndex)
    {
        if (!double.IsFinite(pan) || Math.Abs(pan) < 1e-6) return 1.0;
        double p = Math.Clamp(pan, -1.0, 1.0);
        double angle = (p + 1.0) * (Math.PI * 0.25); // 0..pi/2
        const double invCenter = 1.4142135623730951;  // sqrt(2): Pan=0 ergibt exakt Faktor 1.0 auf L und R
        return (targetChannelIndex & 1) == 0 ? Math.Cos(angle) * invCenter : Math.Sin(angle) * invCenter;
    }

    Section[] BuildSections(ChannelDsp? channel, bool input, List<BiquadCoefficients> coef, List<int> kinds)
    {
        coef.Clear();
        kinds.Clear();
        if (channel != null) FilterDesign.DesignChannel(channel, _sr, input, coef, kinds);
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
        int nIn = Math.Min(_capIn, s.Inputs.Count);
        int nOut = Math.Min(_capOut, s.Outputs.Count);
        double transMs = s.TransitionMs.HasValue && double.IsFinite(s.TransitionMs.Value)
            ? Math.Clamp(s.TransitionMs.Value, 0.0, MaxTransitionMs)
            : RampMs;
        int rampFrames = transMs <= 0 ? 1 : Math.Max(1, (int)Math.Round(_sr * transMs / 1000.0));

        var c = new Compiled
        {
            Settings = s,
            ActiveInputs = nIn,
            ActiveOutputs = nOut,
            RampFrames = rampFrames,
        };

        var ins = s.Inputs;
        var outs = s.Outputs;

        // DeviceMasters Index-Lookup
        int dmCount = Math.Min(MaxDeviceMasters, s.DeviceMasters?.Count ?? 0);
        c.DeviceMasterCount = dmCount;
        Dictionary<Guid, (int Index, double Gain)>? dmLookup = null;
        if (dmCount > 0)
        {
            dmLookup = new Dictionary<Guid, (int, double)>(dmCount);
            for (int d = 0; d < dmCount; d++)
            {
                var dm = s.DeviceMasters![d];
                dmLookup[dm.Id] = (d, DeviceMasterLinear(dm));
            }
        }

        c.InDeviceIndex = new int[_capIn];
        Array.Fill(c.InDeviceIndex, -1);
        c.OutDeviceIndex = new int[_capOut];
        Array.Fill(c.OutDeviceIndex, -1);

        bool soloIn = false, soloOut = false;
        for (int i = 0; i < nIn; i++) if (ins[i]?.Solo == true) soloIn = true;
        for (int o = 0; o < nOut; o++) if (outs[o]?.Solo == true) soloOut = true;

        double globalMaster = double.IsNaN(s.MasterGainDb) ? 0.0 : Dsp.DbToLin(Math.Min(s.MasterGainDb, MaxMasterDb));

        var inGain = new float[_capIn];
        for (int i = 0; i < nIn; i++)
        {
            var ch = ins[i];
            double devGain = 1.0;
            if (ch?.DeviceId is { } did && dmLookup != null && dmLookup.TryGetValue(did, out var dmInfo))
            {
                c.InDeviceIndex[i] = dmInfo.Index;
                devGain = dmInfo.Gain;
            }
            inGain[i] = (float)(ChannelGain(ch, soloIn, includeTrim: true) * devGain);
        }
        c.InGain = inGain;

        c.Cross = new float[_capOut * _capIn];
        var m = s.MatrixDb;
        for (int o = 0; o < nOut; o++)
        {
            for (int i = 0; i < nIn; i++)
            {
                double db = double.NegativeInfinity;
                if (m != null && i < m.Length && m[i] != null && o < m[i].Length) db = m[i][o];
                double lin = Dsp.DbToLin(Math.Min(db, MaxMatrixDb)); // NaN und -inf -> 0
                if (lin <= 0) continue;
                double panMul = PanMultiplier(ins[i]?.Pan ?? 0.0, outs[o]?.DeviceChannelIndex ?? o);
                double v = inGain[i] * lin * panMul;
                if (Math.Abs(v) < 1e-9) v = 0;
                c.Cross[o * _capIn + i] = (float)v;
            }
        }

        c.BusPlan = (s.Buses.Count > 0 || s.BusRoutes.Count > 0)
            ? BusRouting.BuildExecutionPlan(s, _capIn, _capBus, _capOut, inGain)
            : null;

        var coef = new List<BiquadCoefficients>(32);
        var kinds = new List<int>(32);

        c.InSections = new Section[_capIn][];
        c.InDelay = new double[_capIn];
        for (int i = 0; i < _capIn; i++)
        {
            ChannelDsp? ch = i < nIn ? ins[i] : null;
            c.InSections[i] = BuildSections(ch, true, coef, kinds);
            c.InDelay[i] = DelaySamples(ch);
        }

        c.OutSections = new Section[_capOut][];
        c.OutDelay = new double[_capOut];
        c.OutGain = new float[_capOut];
        c.Ceiling = new float[_capOut];
        for (int o = 0; o < _capOut; o++)
        {
            ChannelDsp? ch = o < nOut ? outs[o] : null;
            c.OutSections[o] = BuildSections(ch, false, coef, kinds);
            c.OutDelay[o] = DelaySamples(ch);
            if (ch != null)
            {
                double devGain = 1.0;
                if (ch.DeviceId is { } did && dmLookup != null && dmLookup.TryGetValue(did, out var dmInfo))
                {
                    c.OutDeviceIndex[o] = dmInfo.Index;
                    devGain = dmInfo.Gain;
                }
                double preAmp = ch.Correction is { Bypassed: false } corr && double.IsFinite(corr.PreampDb)
                    ? Dsp.DbToLin(Math.Clamp(corr.PreampDb, -36.0, 12.0))
                    : 1.0;
                double g = ChannelGain(ch, soloOut, includeTrim: false) * devGain * globalMaster * preAmp;
                c.OutGain[o] = Math.Abs(g) < 1e-9 ? 0f : (float)g;
                c.Ceiling[o] = Limiter.CeilingToLinear(ch.CeilingDbfs);
            }
            else
            {
                c.OutGain[o] = 0f;
                c.Ceiling[o] = Limiter.CeilingToLinear(-1.0);
            }
        }
        return c;
    }

    // ------------------------------------------------------------------ Pegel

    public void ReadInputPeaks(Span<float> destination) => ReadPeaks(_inPeaks, destination);
    public void ReadOutputPeaks(Span<float> destination) => ReadPeaks(_outPeaks, destination);

    public void ReadBusPeaks(Span<float> destination)
    {
        var plan = Volatile.Read(ref _cur);
        var bp = plan.BusPlan;
        if (bp == null) { destination.Clear(); return; }
        int count = Math.Min(bp.BusRanges.Length, destination.Length);
        for (int b = 0; b < count; b++)
        {
            var (first, chs) = bp.BusRanges[b];
            float max = 0f;
            if (first >= 0)
            {
                for (int c = 0; c < chs; c++)
                {
                    float p = BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _busSlotPeaks[first + c], 0));
                    if (p > max) max = p;
                }
            }
            destination[b] = max;
        }
        for (int b = count; b < destination.Length; b++) destination[b] = 0f;
    }

    public void ReadDeviceMasterPeaks(Span<float> destination) => ReadPeaks(_devPeaks, destination);

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
        => Process(inputs, outputs, frames, ReadOnlySpan<bool>.Empty);

    public void Process(float[][] inputs, float[][] outputs, int frames, ReadOnlySpan<bool> monitorOnly)
    {
        if (frames <= 0) return;
        var latest = Volatile.Read(ref _latest);
        if (latest != null && !ReferenceEquals(latest, _cur)) Adopt(latest);

        int pos = 0;
        while (pos < frames)
        {
            int n = Math.Min(MaxChunk, frames - pos);
            ProcessChunk(inputs, outputs, pos, n, monitorOnly);
            pos += n;
        }
    }

    void Adopt(Compiled next)
    {
        for (int i = 0; i < _capIn; i++) Transfer(_inRt[i], next.InSections[i]);
        for (int o = 0; o < _capOut; o++) Transfer(_outRt[o], next.OutSections[o]);
        _cur = next;
        _rampLeft = next.RampFrames;
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
            // Struktur geaendert: alte Kette laeuft ~10 ms weiter und wird ueberblendet
            rt.OldSections = os;
            rt.XfadeLeft = _defaultRampLen;
        }
        rt.Sections = ns;
    }

    void ProcessChunk(float[][] inputs, float[][] outputs, int pos, int n, ReadOnlySpan<bool> monitorOnly)
    {
        var plan = _cur;
        int capIn = _capIn, capOut = _capOut, capBus = _capBus;
        int scanIn = _rampLeft > 0 ? capIn : plan.ActiveInputs;
        int scanOut = _rampLeft > 0 ? capOut : plan.ActiveOutputs;
        int rampBefore = _rampLeft;
        int k = rampBefore < n ? rampBefore : n;       // Anzahl Gain-Rampen-Samples in diesem Teilblock
        int biquadRampBefore = Math.Min(rampBefore, _defaultRampLen);
        int kb = biquadRampBefore < n ? biquadRampBefore : n;

        var cross = _crossCur;
        var tgt = plan.Cross;
        var bp = plan.BusPlan;
        bool hasBusWork = bp != null || (_busActiveInPrev && k > 0);
        int activeBusSlots = bp?.TotalSlots ?? (_busActiveInPrev ? capBus : 0);

        var used = _used;
        var spectrum = Volatile.Read(ref _spectrum);
        int spectrumInput = spectrum is { Input: true } ? spectrum.Channel : -1;
        int spectrumOutput = spectrum is { Input: false } ? spectrum.Channel : -1;

        // Welche Eingaenge werden ueberhaupt gebraucht (direkt oder ueber einen Bus)?
        for (int i = 0; i < capIn; i++) used[i] = false;
        for (int o = 0; o < scanOut; o++)
        {
            int b = o * capIn;
            for (int i = 0; i < scanIn; i++)
                if (cross[b + i] != 0f || tgt[b + i] != 0f) used[i] = true;
        }
        if (hasBusWork)
        {
            float[]? inToBusTgt = bp?.InToBus;
            for (int bs = 0; bs < activeBusSlots; bs++)
            {
                int bOff = bs * capIn;
                for (int i = 0; i < scanIn; i++)
                {
                    float tVal = inToBusTgt != null ? inToBusTgt[bOff + i] : 0f;
                    if (_inToBusCur[bOff + i] != 0f || tVal != 0f) used[i] = true;
                }
            }
        }

        // ---- 1. Eingaenge: Pegel mit Fader-Gain, Delay, Filterkette
        int simdCnt = 0;
        var inDevIdx = plan.InDeviceIndex;
        for (int i = 0; i < scanIn; i++)
        {
            var buf = _inBuf[i];
            float[]? src = inputs != null && i < inputs.Length ? inputs[i] : null;
            float peak;
            if (src != null && src.Length >= pos + n) peak = Dsp.SanitizeCopy(src, pos, buf, n, Dsp.HugeIn);
            else { Array.Clear(buf, 0, n); peak = 0f; }
            float faderPeak = peak * Math.Abs(plan.InGain[i]);
            UpdatePeak(_inPeaks, i, faderPeak);
            int dIdx = inDevIdx[i];
            if (dIdx >= 0) UpdatePeak(_devPeaks, dIdx, faderPeak);

            var rt = _inRt[i];
            rt.Delay.Push(buf, n);
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
            rt.Delay.Read(buf, n, plan.InDelay[i], _defaultRampLen);
            if (i == spectrumInput) spectrum!.Before(buf.AsSpan(0, n));
            if (SimdEligible(rt, kb)) _simdList[simdCnt++] = i;
            else RunChain(rt, buf, n, kb, biquadRampBefore);
        }
        RunSimdGroups(_inRt, _inBuf, simdCnt, n);
        if (spectrumInput >= 0 && spectrumInput < scanIn) spectrum!.After(_inBuf[spectrumInput].AsSpan(0, n));
        for (int i = 0; i < Math.Min(monitorOnly.Length, scanIn); i++)
            if (monitorOnly[i]) Array.Clear(_inBuf[i], 0, n);

        // ---- 2a. Mix-Busse: topologische Summierung + Bus-Gain/Mute/Solo + echtes Bus-Metering
        if (hasBusWork)
        {
            float[]? inToBusTgt = bp?.InToBus;
            float[]? busToBusTgt = bp?.BusToBus;
            float[]? slotGainTgt = bp?.SlotGain;

            for (int bs = 0; bs < activeBusSlots; bs++)
            {
                var bBuf = _busBuf[bs];
                bool first = true;
                int inOff = bs * capIn;
                for (int i = 0; i < scanIn; i++)
                {
                    float cur = _inToBusCur[inOff + i];
                    float t = inToBusTgt != null ? inToBusTgt[inOff + i] : 0f;
                    if (cur == 0f && t == 0f) continue;
                    Accumulate(_inBuf[i], bBuf, n, k, rampBefore, cur, t, ref first);
                    if (k > 0 && cur != t) _inToBusCur[inOff + i] = k == rampBefore ? t : cur + (t - cur) / rampBefore * k;
                }
                // Upstream-Busse (topologisch sortiert -> sSlot < bs)
                int bbOff = bs * capBus;
                for (int sSlot = 0; sSlot < bs; sSlot++)
                {
                    float cur = _busToBusCur[bbOff + sSlot];
                    float t = busToBusTgt != null ? busToBusTgt[bbOff + sSlot] : 0f;
                    if (cur == 0f && t == 0f) continue;
                    Accumulate(_busBuf[sSlot], bBuf, n, k, rampBefore, cur, t, ref first);
                    if (k > 0 && cur != t) _busToBusCur[bbOff + sSlot] = k == rampBefore ? t : cur + (t - cur) / rampBefore * k;
                }
                if (first) Array.Clear(bBuf, 0, n);

                // Bus-Fader / Mute / Solo anwenden
                float bgCur = _busGainCur[bs];
                float bgTgt = slotGainTgt != null ? slotGainTgt[bs] : 0f;
                if (bgCur == bgTgt || k == 0)
                {
                    if (bgTgt != 1f) Dsp.Scale(bBuf, 0, n, bgTgt);
                    _busGainCur[bs] = bgTgt;
                }
                else
                {
                    float step = (bgTgt - bgCur) / rampBefore;
                    Dsp.ScaleRamp(bBuf, _rampIdx, k, bgCur, step);
                    if (k < n) Dsp.Scale(bBuf, k, n, bgTgt);
                    _busGainCur[bs] = k == rampBefore ? bgTgt : bgCur + step * k;
                }

                UpdatePeak(_busSlotPeaks, bs, Dsp.Peak(bBuf, 0, n));
            }
            if (bp == null && k == rampBefore) _busActiveInPrev = false;
            else if (bp is { TotalSlots: > 0 }) _busActiveInPrev = true;
        }

        // ---- 2b. Direkte Matrix + Bus-Ausgaenge -> _mixBuf[o]
        float[]? busToOutTgt = bp?.BusToOut;
        for (int o = 0; o < scanOut; o++)
        {
            var mix = _mixBuf[o];
            bool first = true;
            int b = o * capIn;
            for (int i = 0; i < scanIn; i++)
            {
                float cur = cross[b + i], t = tgt[b + i];
                if (cur == 0f && t == 0f) continue;
                Accumulate(_inBuf[i], mix, n, k, rampBefore, cur, t, ref first);
            }
            if (hasBusWork)
            {
                int boOff = o * capBus;
                for (int bs = 0; bs < activeBusSlots; bs++)
                {
                    float cur = _busToOutCur[boOff + bs];
                    float t = busToOutTgt != null ? busToOutTgt[boOff + bs] : 0f;
                    if (cur == 0f && t == 0f) continue;
                    Accumulate(_busBuf[bs], mix, n, k, rampBefore, cur, t, ref first);
                    if (k > 0 && cur != t) _busToOutCur[boOff + bs] = k == rampBefore ? t : cur + (t - cur) / rampBefore * k;
                }
            }
            if (first) Array.Clear(mix, 0, n);
        }
        if (k > 0)
        {
            int limit = scanOut * capIn;
            for (int idx = 0; idx < limit; idx++)
            {
                float cur = cross[idx], t = tgt[idx];
                if (cur != t) cross[idx] = k == rampBefore ? t : cur + (t - cur) / rampBefore * k;
            }
        }

        // ---- 3. Ausgaenge: Filterkette (Crossover, PEQ, REW-Korrektur)
        if (spectrumOutput >= 0 && spectrumOutput < scanOut) spectrum!.Before(_mixBuf[spectrumOutput].AsSpan(0, n));
        simdCnt = 0;
        for (int o = 0; o < scanOut; o++)
        {
            var rt = _outRt[o];
            if (SimdEligible(rt, kb)) _simdList[simdCnt++] = o;
            else RunChain(rt, _mixBuf[o], n, kb, biquadRampBefore);
        }
        RunSimdGroups(_outRt, _mixBuf, simdCnt, n);
        if (spectrumOutput >= 0 && spectrumOutput < scanOut) spectrum!.After(_mixBuf[spectrumOutput].AsSpan(0, n));

        // ---- 4. Ausgaenge: Delay, Kanal-Fader * Device-Master * Global-Master, Brickwall-Limiter, Pegel
        var outDevIdx = plan.OutDeviceIndex;
        var testTone = Volatile.Read(ref _outputTestTone);
        if (testTone != null && testTone.Output >= scanOut)
            Interlocked.CompareExchange(ref _outputTestTone, null, testTone);
        for (int o = 0; o < scanOut; o++)
        {
            var rt = _outRt[o];
            var mix = _mixBuf[o];

            rt.Delay.Push(mix, n);
            rt.Delay.Read(mix, n, plan.OutDelay[o], _defaultRampLen);

            float gt = plan.OutGain[o], gc = rt.GainCur;
            if (testTone?.Output == o) AddOutputTestTone(testTone, mix, n, Math.Max(gt, gc));
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

            float outPeak = Dsp.Peak(dst, dOff, n);
            UpdatePeak(_outPeaks, o, outPeak);
            int dIdx = outDevIdx[o];
            if (dIdx >= 0) UpdatePeak(_devPeaks, dIdx, outPeak);
        }

        // Unbelegte Ausgaenge im gepoolten Modus definiert nullen
        if (outputs != null)
        {
            int maxClear = Math.Min(outputs.Length, capOut);
            for (int o = scanOut; o < maxClear; o++)
            {
                float[]? d = outputs[o];
                if (d != null && d.Length >= pos + n) Array.Clear(d, pos, n);
            }
        }

        if (k > 0) _rampLeft = rampBefore - k;
    }

    void Accumulate(float[] src, float[] dst, int n, int k, int rampBefore, float cur, float t, ref bool first)
    {
        if (cur == t || k == 0)
        {
            Dsp.MulConst(dst, src, 0, n, t, first);
        }
        else
        {
            float step = (t - cur) / rampBefore;
            Dsp.MulRamp(dst, src, _rampIdx, k, cur, step, first);
            if (k < n) Dsp.MulConst(dst, src, k, n, t, first);
        }
        first = false;
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
        int r = _defaultRampLen;
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
