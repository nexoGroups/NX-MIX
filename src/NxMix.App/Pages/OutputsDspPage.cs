using System.Globalization;
using NxMix.App.Model;
using NxMix.App.Ui;
using NxMix.Engine;
using Vortice.Mathematics;

namespace NxMix.App.Pages;

/// <summary>Tab „Outputs / DSP“ (Strg+6):
/// Dedizierter digitaler Lautsprecher-Controller für Ausgangskanäle im laufenden Audio-Betrieb:
/// Live Pre-/Post-DSP-Spektrum + Summenkurve, Frequenzweiche (Butterworth &amp; Linkwitz-Riley LR12..LR48),
/// bis zu 12-bändiger parametrischer Ausgangs-EQ, erweiterter REW-Import mit Headroom-Vorschau sowie Delay (0..1000 ms) &amp; Schutz-Limiter.</summary>
internal sealed class OutputsDspPage : Page
{
    readonly AppModel _m;
    int _outIdx;
    int _peqBandIdx;
    int _compactTab; // 0=Weiche, 1=PEQ, 2=REW, 3=Delay & Schutz
    bool _grid2x2 = true;

    // Kopfleiste
    readonly IconButton _prevOut = new(Icon.ChevronLeft, "Vorheriger Ausgangskanal") { IconSize = 15 };
    readonly Dropdown _outDd = new() { Tooltip = "Ausgangskanal für DSP-Bearbeitung wählen" };
    readonly IconButton _nextOut = new(Icon.ChevronRight, "Nächster Ausgangskanal") { IconSize = 15 };
    readonly Toggle _eqToggle = new("Kurve", () => EqSpectrumGraph.EqColor) { Font = Theme.CaptionStrong, Tooltip = "Berechnete Summen-Filterkurve ein-/ausblenden" };
    readonly Toggle _preToggle = new("Pre", () => EqSpectrumGraph.BeforeColor) { Font = Theme.CaptionStrong, Tooltip = "Live-Spektrum vor Ausgangs-DSP (orange gestrichelt)" };
    readonly Toggle _postToggle = new("Post", () => EqSpectrumGraph.AfterColor) { Font = Theme.CaptionStrong, Tooltip = "Live-Spektrum nach Ausgangs-DSP (türkis)" };

    readonly EqSpectrumGraph _graph = new();
    readonly Segmented _modSeg = new("Weiche", "PEQ (12)", "REW-Korrektur", "Delay & Schutz") { Font = Theme.CaptionStrong };

    // Karte 0: Frequenzweiche (HPF / LPF)
    readonly Dropdown _hpfTypeDd = new() { Tooltip = "Hochpass-Charakteristik & Flankensteilheit" };
    readonly Slider _hpfSlider = new() { Min = 20, Max = 20000, Default = 80, Step = 0, WheelStep = 5 };
    readonly ValueBox _hpfBox = new() { EditHint = "Doppelklick: HPF-Trennfrequenz in Hz (20..20000)" };
    readonly Dropdown _lpfTypeDd = new() { Tooltip = "Tiefpass-Charakteristik & Flankensteilheit" };
    readonly Slider _lpfSlider = new() { Min = 20, Max = 20000, Default = 2200, Step = 0, WheelStep = 10 };
    readonly ValueBox _lpfBox = new() { EditHint = "Doppelklick: LPF-Trennfrequenz in Hz (20..20000)" };

    // Karte 1: Parametrischer Ausgangs-EQ (bis zu 12 Bänder)
    readonly Dropdown _bandDd = new() { Tooltip = "PEQ-Band auswählen" };
    readonly Button _addBandBtn = new("+ Band", ButtonStyle.Tinted) { Font = Theme.MicroStrong, Tooltip = "Weiteres PEQ-Band hinzufügen (bis zu 12)" };
    readonly IconButton _delBandBtn = new(Icon.Trash, "Aktuelles PEQ-Band löschen") { IconSize = 14 };
    readonly Switch _bandSw = new() { Tooltip = "Ausgewähltes PEQ-Band aktivieren / umgehen" };
    readonly Dropdown _peqTypeDd = new() { Tooltip = "Filtertyp des gewählten PEQ-Bandes" };
    readonly Slider _peqFreqSlider = new() { Min = 20, Max = 20000, Default = 1000, Step = 0, WheelStep = 10 };
    readonly ValueBox _peqFreqBox = new() { EditHint = "Doppelklick: Frequenz in Hz" };
    readonly Slider _peqGainSlider = new() { Min = -24, Max = 18, Default = 0, Step = 0.1, WheelStep = 0.5 };
    readonly ValueBox _peqGainBox = new() { EditHint = "Doppelklick: Anhebung/Absenkung in dB (−24..+18)" };
    readonly ValueBox _peqQBox = new() { EditHint = "Doppelklick: Güte Q (0.1..20)" };

    // Karte 2: REW Raum- & Lautsprecherkorrektur
    readonly Button _rewImportBtn = new("Import …", ButtonStyle.Filled, Icon.Import) { Font = Theme.CaptionStrong, Tooltip = "REW Generic-Filterdatei (.txt) mit Vorschau & Headroom-Schutz importieren" };
    readonly IconButton _rewClearBtn = new(Icon.Trash, "Importierte REW-Korrektur entfernen") { IconSize = 14 };
    readonly Switch _rewActiveSw = new() { Tooltip = "Importierte REW-Korrektur aktivieren / Bypass" };
    readonly Slider _rewPreampSlider = new() { Min = -24, Max = 6, Default = 0, Step = 0.1, WheelStep = 0.5 };
    readonly ValueBox _rewPreampBox = new() { EditHint = "Doppelklick: REW-Headroom-Vorabsenkung in dB" };

    // Karte 3: Laufzeit (Delay) & Lautsprecher-Schutz
    readonly Switch _phaseSw = new() { Tooltip = "Polarität (180° Phasenumkehr) für diesen Ausgang" };
    readonly Slider _delaySlider = new() { Min = 0, Max = 50, Default = 0, Step = 0.02, WheelStep = 0.1 };
    readonly ValueBox _delayBox = new() { EditHint = "Doppelklick: Laufzeit 0..1000 ms präzise eingeben" };
    readonly Dropdown _ceilDd = new() { Tooltip = "Sicherheits-Brickwall-Limiter-Schwelle (dBFS)" };
    readonly Dropdown _lockDd = new() { Tooltip = "Maximale Ausgangsverstärkung (Hochtöner-Schutz)" };

    RectF[] _cardRects = new RectF[4];
    RectF _rewInfoRect, _delayDistRect;

    readonly record struct XoverChoice(string Label, int Order, bool LinkwitzRiley);
    static readonly XoverChoice[] s_xoverChoices =
    {
        new("Aus", 0, false),
        new("Butterworth 12 dB", 2, false),
        new("Butterworth 24 dB", 4, false),
        new("LR 12 dB (LR12)", 2, true),
        new("LR 24 dB (LR24)", 4, true),
        new("LR 36 dB (LR36)", 6, true),
        new("LR 48 dB (LR48)", 8, true),
    };

    static readonly (FilterType Type, string Label, string Short)[] s_peqTypes =
    {
        (FilterType.Peak, "Peak / Bell (PK)", "PK"),
        (FilterType.LowShelf, "Low-Shelf (LS)", "LS"),
        (FilterType.HighShelf, "High-Shelf (HS)", "HS"),
        (FilterType.Notch, "Notch / Kerbfilter", "NO"),
        (FilterType.AllPass, "All-Pass (Phase)", "AP"),
    };

    static readonly double[] s_ceilings = { -1, -3, -6, -12 };
    static readonly (double MaxDb, string Label)[] s_locks =
    {
        (Theme.FaderMaxDb, "Aus (+12 dB)"),
        (0, "Max 0 dB"),
        (-6, "Max −6 dB"),
        (-12, "Max −12 dB"),
        (-20, "Max −20 dB"),
    };

    public override string Title => "Outputs / DSP";
    public override Icon Icon => Icon.Dsp;
    public override string? Subtitle => _m.OutputCount > 0
        ? $"Ausgang {_outIdx + 1:00}: {_m.DisplayName(false, ClampedOutIdx)}  ·  Live-Weiche, PEQ, REW & Laufzeit ohne Audio-Stopp"
        : "Keine Ausgangskanäle konfiguriert";

    int ClampedOutIdx => _m.OutputCount == 0 ? 0 : Math.Clamp(_outIdx, 0, _m.OutputCount - 1);

    public OutputsDspPage(AppModel m)
    {
        _m = m;
        Add(_prevOut); Add(_outDd); Add(_nextOut);
        Add(_eqToggle); Add(_preToggle); Add(_postToggle);
        Add(_graph);
        Add(_modSeg);

        Add(_hpfTypeDd); Add(_hpfSlider); Add(_hpfBox);
        Add(_lpfTypeDd); Add(_lpfSlider); Add(_lpfBox);

        Add(_bandDd); Add(_addBandBtn); Add(_delBandBtn); Add(_bandSw);
        Add(_peqTypeDd); Add(_peqFreqSlider); Add(_peqFreqBox);
        Add(_peqGainSlider); Add(_peqGainBox); Add(_peqQBox);

        Add(_rewImportBtn); Add(_rewClearBtn); Add(_rewActiveSw);
        Add(_rewPreampSlider); Add(_rewPreampBox);

        Add(_phaseSw); Add(_delaySlider); Add(_delayBox);
        Add(_ceilDd); Add(_lockDd);

        // Logarithmische Frequenz-Skala (20 Hz .. 20 kHz)
        static double FreqToNorm(double hz) => Math.Clamp(Math.Log(Math.Clamp(hz, 20, 20000) / 20.0) / Math.Log(1000.0), 0, 1);
        static double NormToFreq(double t) => Math.Round(20.0 * Math.Pow(1000.0, Math.Clamp(t, 0, 1)));
        _hpfSlider.ToNorm = FreqToNorm; _hpfSlider.FromNorm = NormToFreq;
        _lpfSlider.ToNorm = FreqToNorm; _lpfSlider.FromNorm = NormToFreq;
        _peqFreqSlider.ToNorm = FreqToNorm; _peqFreqSlider.FromNorm = NormToFreq;

        _eqToggle.SetChecked(true);
        _preToggle.SetChecked(true);
        _postToggle.SetChecked(true);
        _eqToggle.Toggled += on => { _graph.ShowEq = on; Invalidate(); };
        _preToggle.Toggled += on => { _graph.ShowBefore = on; Invalidate(); };
        _postToggle.Toggled += on => { _graph.ShowAfter = on; Invalidate(); };

        _prevOut.Clicked += _ => SelectOutputChannel(ClampedOutIdx - 1);
        _nextOut.Clicked += _ => SelectOutputChannel(ClampedOutIdx + 1);
        _outDd.Text = () =>
        {
            if (_m.OutputCount == 0) return "Kein Ausgang vorhanden";
            int idx = ClampedOutIdx;
            var ch = _m.Channel(false, idx);
            string owner = _m.ChannelOwnerTitle(ch);
            return $"OUT {idx + 1:00} · {_m.DisplayName(false, idx)}" + (owner.Length > 0 ? $" ({owner})" : "");
        };
        _outDd.Items = BuildOutputMenu;

        _modSeg.Changed += idx => { _compactTab = idx; Layout(); Invalidate(); };

        WireCrossoverControls();
        WirePeqControls();
        WireRewControls();
        WireDelayProtectionControls();

        m.ValuesChanged += OnModelValues;
        m.StructureChanged += () => { _outIdx = ClampedOutIdx; Layout(); Invalidate(); };
        m.HostChanged += () => Invalidate();
    }

    public void SelectOutputChannel(int outputChannelIndex)
    {
        if (_m.OutputCount == 0) { _outIdx = 0; return; }
        _outIdx = Math.Clamp(outputChannelIndex, 0, _m.OutputCount - 1);
        _peqBandIdx = 0;
        if (Visible)
        {
            _graph.Update(_m.Channel(false, _outIdx), _m.Settings.SampleRate, false, _outIdx, _m, 0f);
            Layout();
            Invalidate();
        }
    }

    public void ApplyRewPreview(int outputChannelIndex, RewImportPreview preview, bool applyHeadroomTrim)
    {
        if (outputChannelIndex < 0 || outputChannelIndex >= _m.OutputCount) return;
        var corr = RewFilterImport.Build(preview, applyHeadroomTrim);
        _m.UpdateChannel(false, outputChannelIndex, c => c with { Correction = corr });
    }

    void OnModelValues()
    {
        if (_m.OutputCount > 0)
            _graph.RefreshCurve(_m.Channel(false, ClampedOutIdx), _m.Settings.SampleRate, false);
        if (Visible) { Layout(); Invalidate(); }
    }

    public override void OnShow()
    {
        _outIdx = ClampedOutIdx;
        if (_m.OutputCount > 0)
            _graph.Update(_m.Channel(false, _outIdx), _m.Settings.SampleRate, false, _outIdx, _m, 0f);
        Layout();
    }

    public override void Tick(float dt)
    {
        if (_m.OutputCount == 0) return;
        int idx = ClampedOutIdx;
        if (_graph.Update(_m.Channel(false, idx), _m.Settings.SampleRate, false, idx, _m, dt))
            Invalidate();
    }

    List<MenuItem> BuildOutputMenu()
    {
        var items = new List<MenuItem>();
        for (int i = 0; i < _m.OutputCount; i++)
        {
            int idx = i;
            var ch = _m.Channel(false, idx);
            string owner = _m.ChannelOwnerTitle(ch);
            items.Add(new MenuItem(
                $"OUT {idx + 1:00} · {_m.DisplayName(false, idx)}",
                owner.Length > 0 ? owner : null,
                () => SelectOutputChannel(idx))
            {
                Checked = idx == ClampedOutIdx,
            });
        }
        return items;
    }

    // ---- Helfer zum Aufteilen von Crossover & PEQ in ChannelDsp.Filters -----------------------
    static FilterSpec? GetXover(ChannelDsp c, bool highPass) =>
        c.Filters.FirstOrDefault(f => f.Type == (highPass ? FilterType.HighPass : FilterType.LowPass));

    static List<FilterSpec> GetPeqBands(ChannelDsp c) =>
        c.Filters.Where(f => f.Type is not FilterType.HighPass and not FilterType.LowPass).ToList();

    static IReadOnlyList<FilterSpec> ComposeFilters(FilterSpec? hpf, FilterSpec? lpf, IReadOnlyList<FilterSpec> peq)
    {
        var list = new List<FilterSpec>(peq.Count + 2);
        if (hpf != null) list.Add(hpf);
        if (lpf != null) list.Add(lpf);
        list.AddRange(peq);
        return list;
    }

    static string FormatXoverChoice(FilterSpec? f)
    {
        if (f == null || !f.Enabled) return "Aus";
        foreach (var ch in s_xoverChoices)
            if (ch.Order == f.Order && ch.LinkwitzRiley == f.LinkwitzRiley) return ch.Label;
        return f.LinkwitzRiley ? $"LR {f.Order * 6} dB" : $"BW {f.Order * 6} dB";
    }

    static string FormatHz(double hz) =>
        hz >= 1000 ? $"{hz / 1000.0:0.##} kHz" : $"{hz:0} Hz";

    static bool TryParseHz(string text, out double hz)
    {
        string s = text.Trim().ToLowerInvariant().Replace(',', '.');
        double mult = 1.0;
        if (s.EndsWith("khz")) { mult = 1000.0; s = s[..^3].Trim(); }
        else if (s.EndsWith("k")) { mult = 1000.0; s = s[..^1].Trim(); }
        else if (s.EndsWith("hz")) { s = s[..^2].Trim(); }
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v))
        {
            hz = Math.Clamp(v * mult, 20.0, 20000.0);
            return true;
        }
        hz = 0;
        return false;
    }

    void UpdateCurrentOutput(Func<ChannelDsp, ChannelDsp> fn)
    {
        if (_m.OutputCount == 0) return;
        _m.UpdateChannel(false, ClampedOutIdx, fn);
    }

    void WireCrossoverControls()
    {
        _hpfTypeDd.Text = () => _m.OutputCount == 0 ? "Aus" : FormatXoverChoice(GetXover(_m.Channel(false, ClampedOutIdx), true));
        _hpfTypeDd.Items = () => BuildXoverMenu(highPass: true);
        _hpfSlider.Changed += hz => SetXoverFreq(true, hz);
        _hpfBox.Typed += s => { if (TryParseHz(s, out double hz)) SetXoverFreq(true, hz); };

        _lpfTypeDd.Text = () => _m.OutputCount == 0 ? "Aus" : FormatXoverChoice(GetXover(_m.Channel(false, ClampedOutIdx), false));
        _lpfTypeDd.Items = () => BuildXoverMenu(highPass: false);
        _lpfSlider.Changed += hz => SetXoverFreq(false, hz);
        _lpfBox.Typed += s => { if (TryParseHz(s, out double hz)) SetXoverFreq(false, hz); };
    }

    List<MenuItem> BuildXoverMenu(bool highPass)
    {
        var cur = _m.OutputCount > 0 ? GetXover(_m.Channel(false, ClampedOutIdx), highPass) : null;
        var items = new List<MenuItem>();
        foreach (var choice in s_xoverChoices)
        {
            var ch = choice;
            bool isChecked = ch.Order == 0 ? (cur == null || !cur.Enabled) : (cur is { Enabled: true } && cur.Order == ch.Order && cur.LinkwitzRiley == ch.LinkwitzRiley);
            items.Add(new MenuItem(ch.Label, null, () =>
            {
                UpdateCurrentOutput(c =>
                {
                    var hpf = GetXover(c, true);
                    var lpf = GetXover(c, false);
                    var peq = GetPeqBands(c);
                    FilterSpec? next = ch.Order == 0
                        ? null
                        : new FilterSpec(
                            highPass ? FilterType.HighPass : FilterType.LowPass,
                            (highPass ? hpf?.FrequencyHz : lpf?.FrequencyHz) ?? (highPass ? 80.0 : 2200.0),
                            0.7071, 0, ch.Order, ch.LinkwitzRiley, Enabled: true);
                    return c with { Filters = ComposeFilters(highPass ? next : hpf, highPass ? lpf : next, peq) };
                });
            })
            { Checked = isChecked });
        }
        return items;
    }

    void SetXoverFreq(bool highPass, double hz)
    {
        UpdateCurrentOutput(c =>
        {
            var hpf = GetXover(c, true);
            var lpf = GetXover(c, false);
            var peq = GetPeqBands(c);
            var target = highPass ? hpf : lpf;
            var updated = target != null
                ? target with { FrequencyHz = hz, Enabled = true }
                : new FilterSpec(highPass ? FilterType.HighPass : FilterType.LowPass, hz, 0.7071, 0, Order: 4, LinkwitzRiley: true, Enabled: true);
            return c with { Filters = ComposeFilters(highPass ? updated : hpf, highPass ? lpf : updated, peq) };
        });
    }

    void WirePeqControls()
    {
        _bandDd.Text = () =>
        {
            if (_m.OutputCount == 0) return "Kein Band";
            var bands = GetPeqBands(_m.Channel(false, ClampedOutIdx));
            if (bands.Count == 0) return "Keine Bänder";
            int idx = Math.Clamp(_peqBandIdx, 0, bands.Count - 1);
            var b = bands[idx];
            string st = s_peqTypes.FirstOrDefault(t => t.Type == b.Type).Short ?? "PK";
            return $"B{idx + 1}/{bands.Count} · {st}";
        };
        _bandDd.Items = () =>
        {
            var items = new List<MenuItem>();
            if (_m.OutputCount == 0) return items;
            var bands = GetPeqBands(_m.Channel(false, ClampedOutIdx));
            for (int i = 0; i < bands.Count; i++)
            {
                int idx = i;
                var b = bands[idx];
                string st = s_peqTypes.FirstOrDefault(t => t.Type == b.Type).Short ?? "PK";
                items.Add(new MenuItem($"Band {idx + 1}: {st} @ {FormatHz(b.FrequencyHz)}", Db.Format(b.GainDb), () =>
                {
                    _peqBandIdx = idx;
                    Layout();
                    Invalidate();
                })
                { Checked = idx == Math.Clamp(_peqBandIdx, 0, bands.Count - 1) });
            }
            return items;
        };

        _addBandBtn.Clicked += () =>
        {
            if (_m.OutputCount == 0) return;
            UpdateCurrentOutput(c =>
            {
                var bands = GetPeqBands(c);
                if (bands.Count >= 12) return c;
                double[] defaultFreqs = { 100, 250, 630, 1200, 2500, 5000, 8000, 12000, 63, 160, 400, 10000 };
                double f = defaultFreqs[bands.Count % defaultFreqs.Length];
                bands.Add(new FilterSpec(FilterType.Peak, f, 1.41, 0.0, Enabled: true));
                _peqBandIdx = bands.Count - 1;
                return c with { Filters = ComposeFilters(GetXover(c, true), GetXover(c, false), bands) };
            });
        };

        _delBandBtn.Clicked += _ =>
        {
            if (_m.OutputCount == 0) return;
            UpdateCurrentOutput(c =>
            {
                var bands = GetPeqBands(c);
                if (bands.Count == 0) return c;
                int idx = Math.Clamp(_peqBandIdx, 0, bands.Count - 1);
                bands.RemoveAt(idx);
                _peqBandIdx = Math.Clamp(_peqBandIdx, 0, Math.Max(0, bands.Count - 1));
                return c with { Filters = ComposeFilters(GetXover(c, true), GetXover(c, false), bands) };
            });
        };

        _bandSw.Changed += on => ModifySelectedBand(b => b with { Enabled = on });

        _peqTypeDd.Text = () =>
        {
            var b = CurrentBand();
            if (b == null) return "Peak (PK)";
            return s_peqTypes.FirstOrDefault(t => t.Type == b.Type).Label ?? b.Type.ToString();
        };
        _peqTypeDd.Items = () =>
        {
            var b = CurrentBand();
            return s_peqTypes.Select(t => new MenuItem(t.Label, null, () => ModifySelectedBand(x => x with { Type = t.Type }))
            {
                Checked = b?.Type == t.Type,
            }).ToList();
        };

        _peqFreqSlider.Changed += hz => ModifySelectedBand(b => b with { FrequencyHz = hz });
        _peqFreqBox.Typed += s => { if (TryParseHz(s, out double hz)) ModifySelectedBand(b => b with { FrequencyHz = hz }); };

        _peqGainSlider.Changed += db => ModifySelectedBand(b => b with { GainDb = Math.Round(db * 10) / 10 });
        _peqGainBox.Typed += s => { if (Db.TryParse(s, out double db)) ModifySelectedBand(b => b with { GainDb = Math.Clamp(db, -24, 18) }); };

        _peqQBox.Typed += s =>
        {
            string clean = s.Trim().Replace("Q", "", StringComparison.OrdinalIgnoreCase).Trim().Replace(',', '.');
            if (double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out double q) && double.IsFinite(q))
                ModifySelectedBand(b => b with { Q = Math.Clamp(q, 0.1, 20.0) });
        };
    }

    FilterSpec? CurrentBand()
    {
        if (_m.OutputCount == 0) return null;
        var bands = GetPeqBands(_m.Channel(false, ClampedOutIdx));
        if (bands.Count == 0) return null;
        return bands[Math.Clamp(_peqBandIdx, 0, bands.Count - 1)];
    }

    void ModifySelectedBand(Func<FilterSpec, FilterSpec> fn)
    {
        UpdateCurrentOutput(c =>
        {
            var bands = GetPeqBands(c);
            if (bands.Count == 0) return c;
            int idx = Math.Clamp(_peqBandIdx, 0, bands.Count - 1);
            bands[idx] = fn(bands[idx]);
            return c with { Filters = ComposeFilters(GetXover(c, true), GetXover(c, false), bands) };
        });
    }

    void WireRewControls()
    {
        _rewImportBtn.Clicked += ImportRewInteractive;
        _rewClearBtn.Clicked += _ =>
        {
            UpdateCurrentOutput(c => c with { Correction = null });
            Root?.Notify.Flash("REW-Korrektur von diesem Ausgang entfernt.");
        };
        _rewActiveSw.Changed += on =>
        {
            UpdateCurrentOutput(c => c.Correction == null ? c : c with { Correction = c.Correction with { Bypassed = !on } });
        };
        _rewPreampSlider.Changed += db =>
        {
            UpdateCurrentOutput(c => c.Correction == null ? c : c with { Correction = c.Correction with { PreampDb = Math.Round(db * 10) / 10 } });
        };
        _rewPreampBox.Typed += s =>
        {
            if (Db.TryParse(s, out double db))
                UpdateCurrentOutput(c => c.Correction == null ? c : c with { Correction = c.Correction with { PreampDb = Math.Clamp(db, -24, 6) } });
        };
    }

    void ImportRewInteractive()
    {
        if (Root is not { } root || _m.OutputCount == 0) return;
        int targetOut = ClampedOutIdx;
        string? path = root.Shell.ShowOpenDialog("REW Generic-Filterdatei importieren", "REW-Textdatei (*.txt)|*.txt|Alle Dateien (*.*)|*.*");
        if (string.IsNullOrWhiteSpace(path)) return;

        RewImportPreview preview;
        try
        {
            preview = RewFilterImport.ReadPreview(path, _m.Settings.SampleRate);
        }
        catch (Exception ex)
        {
            ModalDialog.Show(root, "REW-Import fehlgeschlagen", ex.Message, new[] { "Schließen" }, 0, -1, _ => { }, Icon.Warn);
            return;
        }

        string types = preview.TypeSummary.Count > 0 ? string.Join(", ", preview.TypeSummary) : $"{preview.Filters.Count} Filter";
        string msg =
            $"Datei: {preview.SourceName}\n" +
            $"Erkannte aktive Filter: {preview.Filters.Count}" + (preview.SkippedCount > 0 ? $" ({preview.SkippedCount} deaktivierte/übersprungene Zeilen)" : "") + "\n" +
            $"Filtertypen: {types}\n" +
            $"Maximaler Summen-Boost: {(preview.MaxBoostDb > 0.05 ? $"+{preview.MaxBoostDb:0.1} dB" : "0.0 dB (keine Anhebung)")}";

        if (preview.MaxBoostDb > 0.15)
        {
            msg += $"\n\nEmpfehlung: Mit Headroom-Schutz ({preview.RecommendedPreampDb:0.1} dB Preamp) übernehmen, um digitales Clipping vor dem Ausgangs-Wandler sicher zu verhindern.";
            ModalDialog.Show(
                root,
                "REW-Korrektur importieren",
                msg,
                new[] { "Abbrechen", "Ohne Preamp (0 dB)", $"Mit Headroom ({preview.RecommendedPreampDb:0.1} dB)" },
                defaultButton: 2,
                dangerButton: -1,
                res =>
                {
                    if (res == 1)
                    {
                        ApplyRewPreview(targetOut, preview, applyHeadroomTrim: false);
                        root.Notify.Flash($"REW „{preview.SourceName}“ ({preview.Filters.Count} Filter) ohne Preamp geladen.");
                    }
                    else if (res == 2)
                    {
                        ApplyRewPreview(targetOut, preview, applyHeadroomTrim: true);
                        root.Notify.Flash($"REW „{preview.SourceName}“ mit {preview.RecommendedPreampDb:0.1} dB Headroom-Schutz aktiviert.");
                    }
                },
                Icon.Import);
        }
        else
        {
            ModalDialog.Show(
                root,
                "REW-Korrektur importieren",
                msg,
                new[] { "Abbrechen", "Übernehmen" },
                defaultButton: 1,
                dangerButton: -1,
                res =>
                {
                    if (res == 1)
                    {
                        ApplyRewPreview(targetOut, preview, applyHeadroomTrim: false);
                        root.Notify.Flash($"REW „{preview.SourceName}“ ({preview.Filters.Count} Filter) aktiviert.");
                    }
                },
                Icon.Import);
        }
    }

    void WireDelayProtectionControls()
    {
        _phaseSw.Changed += on => UpdateCurrentOutput(c => c with { InvertPhase = on });
        _delaySlider.Changed += ms => UpdateCurrentOutput(c => c with { DelayMs = Math.Round(ms * 100) / 100 });
        _delayBox.Typed += s =>
        {
            string clean = s.Trim().Replace("ms", "", StringComparison.OrdinalIgnoreCase).Trim().Replace(',', '.');
            if (double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out double ms) && double.IsFinite(ms))
                UpdateCurrentOutput(c => c with { DelayMs = Math.Clamp(ms, 0.0, 1000.0) });
        };

        _ceilDd.Text = () => _m.OutputCount == 0 ? "−1 dBFS" : $"{_m.Channel(false, ClampedOutIdx).CeilingDbfs:0} dBFS".Replace('-', Db.Minus);
        _ceilDd.Items = () =>
        {
            double cur = _m.OutputCount > 0 ? _m.Channel(false, ClampedOutIdx).CeilingDbfs : -1;
            return s_ceilings.Select(v => new MenuItem($"Ceiling {v:0} dBFS".Replace('-', Db.Minus), null,
                () => UpdateCurrentOutput(c => c with { CeilingDbfs = v }))
            {
                Checked = Math.Abs(cur - v) < 0.25,
            }).ToList();
        };

        _lockDd.Text = () =>
        {
            if (_m.OutputCount == 0) return "Aus";
            double max = _m.Channel(false, ClampedOutIdx).MaxGainDb;
            foreach (var l in s_locks) if (Math.Abs(max - l.MaxDb) < 0.3) return l.Label;
            return $"Max {Db.Format(max)}";
        };
        _lockDd.Items = () =>
        {
            double cur = _m.OutputCount > 0 ? _m.Channel(false, ClampedOutIdx).MaxGainDb : Theme.FaderMaxDb;
            return s_locks.Select(l => new MenuItem(l.Label, null,
                () => UpdateCurrentOutput(c => c with { MaxGainDb = l.MaxDb, GainDb = Math.Min(c.GainDb, l.MaxDb) }))
            {
                Checked = Math.Abs(cur - l.MaxDb) < 0.3,
            }).ToList();
        };
    }

    void SetCardWidgetsVisible(int cardIdx, bool vis)
    {
        switch (cardIdx)
        {
            case 0:
                _hpfTypeDd.Visible = _hpfSlider.Visible = _hpfBox.Visible = vis;
                _lpfTypeDd.Visible = _lpfSlider.Visible = _lpfBox.Visible = vis;
                break;
            case 1:
                _bandDd.Visible = _addBandBtn.Visible = _delBandBtn.Visible = _bandSw.Visible = vis;
                _peqTypeDd.Visible = _peqFreqSlider.Visible = _peqFreqBox.Visible = vis;
                _peqGainSlider.Visible = _peqGainBox.Visible = _peqQBox.Visible = vis;
                break;
            case 2:
                _rewImportBtn.Visible = _rewClearBtn.Visible = _rewActiveSw.Visible = vis;
                _rewPreampSlider.Visible = _rewPreampBox.Visible = vis;
                break;
            case 3:
                _phaseSw.Visible = _delaySlider.Visible = _delayBox.Visible = vis;
                _ceilDd.Visible = _lockDd.Visible = vis;
                break;
        }
    }

    public override void Layout()
    {
        var area = Bounds.Deflate(Theme.Pad, 8, Theme.Pad, Theme.Pad);
        float gap = Theme.Gap;
        float ch = Theme.ControlHSmall;

        // Kopfleiste
        float topY = area.Y;
        float togW = 58f;
        _postToggle.Bounds = new RectF(area.Right - togW, topY, togW, ch);
        _preToggle.Bounds = new RectF(_postToggle.Bounds.X - 6 - togW, topY, togW, ch);
        _eqToggle.Bounds = new RectF(_preToggle.Bounds.X - 6 - togW, topY, togW, ch);

        _prevOut.Bounds = new RectF(area.X, topY, ch, ch);
        float maxDdW = Math.Max(120f, _eqToggle.Bounds.X - (_prevOut.Bounds.Right + 6) - ch - 12);
        float ddW = Math.Min(320f, maxDdW);
        _outDd.Bounds = new RectF(_prevOut.Bounds.Right + 4, topY, ddW, ch);
        _nextOut.Bounds = new RectF(_outDd.Bounds.Right + 4, topY, ch, ch);

        bool hasOut = _m.OutputCount > 0;
        _prevOut.Enabled = hasOut && ClampedOutIdx > 0;
        _nextOut.Enabled = hasOut && ClampedOutIdx + 1 < _m.OutputCount;
        _outDd.Enabled = hasOut;

        // Spektrum-Graph oben
        float graphY = topY + ch + 8;
        float availAfterTop = Math.Max(220f, area.Bottom - graphY);
        float graphH = Math.Clamp(availAfterTop * 0.38f, 122f, 240f);
        _graph.Bounds = new RectF(area.X, graphY, area.W, graphH);

        float rackTop = _graph.Bounds.Bottom + 8;
        float remH = Math.Max(120f, area.Bottom - rackTop);
        _grid2x2 = remH >= 316f && area.W >= 860f;
        _modSeg.Visible = !_grid2x2;

        if (_grid2x2)
        {
            float cw = MathF.Floor((area.W - gap) / 2f);
            float rh = MathF.Floor((remH - gap) / 2f);
            _cardRects[0] = new RectF(area.X, rackTop, cw, rh);
            _cardRects[1] = new RectF(area.X + cw + gap, rackTop, area.W - cw - gap, rh);
            _cardRects[2] = new RectF(area.X, rackTop + rh + gap, cw, remH - rh - gap);
            _cardRects[3] = new RectF(area.X + cw + gap, rackTop + rh + gap, area.W - cw - gap, remH - rh - gap);
            for (int k = 0; k < 4; k++) SetCardWidgetsVisible(k, true);
        }
        else
        {
            _modSeg.SetSelected(_compactTab);
            _modSeg.Bounds = new RectF(area.X, rackTop, area.W, ch);
            var single = new RectF(area.X, _modSeg.Bounds.Bottom + 6, area.W, Math.Max(110f, area.Bottom - (_modSeg.Bounds.Bottom + 6)));
            for (int k = 0; k < 4; k++)
            {
                _cardRects[k] = single;
                SetCardWidgetsVisible(k, k == _compactTab);
            }
        }

        var c = hasOut ? _m.Channel(false, ClampedOutIdx) : new ChannelDsp();
        LayoutCard0Crossover(_cardRects[0], c, hasOut);
        LayoutCard1Peq(_cardRects[1], c, hasOut);
        LayoutCard2Rew(_cardRects[2], c, hasOut);
        LayoutCard3DelayProt(_cardRects[3], c, hasOut);
    }

    void LayoutCard0Crossover(RectF b, ChannelDsp c, bool hasOut)
    {
        float pad = 12f;
        float ch = Theme.ControlHSmall;
        var hpf = GetXover(c, true);
        var lpf = GetXover(c, false);

        float y1 = b.Y + 40;
        float y2 = y1 + ch + 10;
        float ddW = Math.Clamp(b.W * 0.36f, 128f, 174f);
        float boxW = 68f;

        _hpfTypeDd.Enabled = _lpfTypeDd.Enabled = hasOut;
        bool hpfOn = hasOut && hpf is { Enabled: true };
        bool lpfOn = hasOut && lpf is { Enabled: true };
        _hpfSlider.Enabled = _hpfBox.Enabled = hpfOn;
        _lpfSlider.Enabled = _lpfBox.Enabled = lpfOn;

        double hpfHz = hpf?.FrequencyHz ?? 80.0;
        double lpfHz = lpf?.FrequencyHz ?? 2200.0;
        _hpfSlider.SetValue(hpfHz);
        _hpfBox.Text = FormatHz(hpfHz);
        _lpfSlider.SetValue(lpfHz);
        _lpfBox.Text = FormatHz(lpfHz);

        _hpfTypeDd.Bounds = new RectF(b.X + pad + 36, y1, ddW, ch);
        _hpfBox.Bounds = new RectF(b.Right - pad - boxW, y1, boxW, ch);
        _hpfSlider.Bounds = new RectF(_hpfTypeDd.Bounds.Right + 8, y1, Math.Max(48, _hpfBox.Bounds.X - _hpfTypeDd.Bounds.Right - 16), ch);

        _lpfTypeDd.Bounds = new RectF(b.X + pad + 36, y2, ddW, ch);
        _lpfBox.Bounds = new RectF(b.Right - pad - boxW, y2, boxW, ch);
        _lpfSlider.Bounds = new RectF(_lpfTypeDd.Bounds.Right + 8, y2, Math.Max(48, _lpfBox.Bounds.X - _lpfTypeDd.Bounds.Right - 16), ch);
    }

    void LayoutCard1Peq(RectF b, ChannelDsp c, bool hasOut)
    {
        float pad = 12f;
        float ch = Theme.ControlHSmall;
        var bands = GetPeqBands(c);
        bool hasBand = hasOut && bands.Count > 0;
        FilterSpec band = hasBand ? bands[Math.Clamp(_peqBandIdx, 0, bands.Count - 1)] : new FilterSpec(FilterType.Peak, 1000, 1.41, 0);

        float y0 = b.Y + 8;
        _bandSw.Enabled = hasBand;
        _bandSw.SetOn(band.Enabled);
        _bandSw.Bounds = new RectF(b.Right - pad - Theme.SwitchW, y0, Theme.SwitchW, ch);
        _delBandBtn.Enabled = hasBand;
        _delBandBtn.Bounds = new RectF(_bandSw.Bounds.X - 6 - ch, y0, ch, ch);
        _addBandBtn.Enabled = hasOut && bands.Count < 12;
        _addBandBtn.Bounds = new RectF(_delBandBtn.Bounds.X - 4 - 64, y0, 64, ch);
        float bandDdW = Math.Clamp(_addBandBtn.Bounds.X - (b.X + pad + 56) - 6, 86f, 136f);
        _bandDd.Enabled = hasBand;
        _bandDd.Bounds = new RectF(_addBandBtn.Bounds.X - 6 - bandDdW, y0, bandDdW, ch);

        float y1 = y0 + ch + 8;
        _peqTypeDd.Enabled = _peqFreqSlider.Enabled = _peqFreqBox.Enabled = hasBand;
        _peqFreqSlider.SetValue(band.FrequencyHz);
        _peqFreqBox.Text = FormatHz(band.FrequencyHz);
        float typeW = Math.Clamp(b.W * 0.30f, 108f, 146f);
        _peqTypeDd.Bounds = new RectF(b.X + pad, y1, typeW, ch);
        _peqFreqBox.Bounds = new RectF(b.Right - pad - 68, y1, 68, ch);
        _peqFreqSlider.Bounds = new RectF(_peqTypeDd.Bounds.Right + 8, y1, Math.Max(48, _peqFreqBox.Bounds.X - _peqTypeDd.Bounds.Right - 16), ch);

        float y2 = y1 + ch + 8;
        _peqGainSlider.Enabled = _peqGainBox.Enabled = hasBand && band.Type is FilterType.Peak or FilterType.LowShelf or FilterType.HighShelf;
        _peqQBox.Enabled = hasBand;
        _peqGainSlider.SetValue(band.GainDb);
        _peqGainBox.Text = Db.Format(band.GainDb);
        _peqQBox.Text = $"Q {band.Q:0.##}";

        _peqQBox.Bounds = new RectF(b.Right - pad - 58, y2, 58, ch);
        _peqGainBox.Bounds = new RectF(_peqQBox.Bounds.X - 8 - 64, y2, 64, ch);
        _peqGainSlider.Bounds = new RectF(b.X + pad + 38, y2, Math.Max(48, _peqGainBox.Bounds.X - (b.X + pad + 38) - 8), ch);
    }

    void LayoutCard2Rew(RectF b, ChannelDsp c, bool hasOut)
    {
        float pad = 12f;
        float ch = Theme.ControlHSmall;
        var corr = c.Correction;
        bool hasCorr = hasOut && corr != null;

        float y0 = b.Y + 8;
        _rewClearBtn.Enabled = hasCorr;
        _rewClearBtn.Bounds = new RectF(b.Right - pad - ch, y0, ch, ch);
        _rewImportBtn.Enabled = hasOut;
        _rewImportBtn.Bounds = new RectF(_rewClearBtn.Bounds.X - 6 - 96, y0, 96, ch);

        _rewInfoRect = new RectF(b.X + pad, y0 + ch + 6, b.W - 2 * pad, 26);

        float y2 = _rewInfoRect.Bottom + 6;
        _rewActiveSw.Enabled = _rewPreampSlider.Enabled = _rewPreampBox.Enabled = hasCorr;
        _rewActiveSw.SetOn(corr is { Bypassed: false });
        double preamp = corr?.PreampDb ?? 0.0;
        _rewPreampSlider.SetValue(preamp);
        _rewPreampBox.Text = Db.Format(preamp);

        _rewActiveSw.Bounds = new RectF(b.X + pad + 40, y2, Theme.SwitchW, ch);
        _rewPreampBox.Bounds = new RectF(b.Right - pad - 64, y2, 64, ch);
        float slX = _rewActiveSw.Bounds.Right + 58;
        _rewPreampSlider.Bounds = new RectF(slX, y2, Math.Max(48, _rewPreampBox.Bounds.X - slX - 8), ch);
    }

    void LayoutCard3DelayProt(RectF b, ChannelDsp c, bool hasOut)
    {
        float pad = 12f;
        float ch = Theme.ControlHSmall;

        float y0 = b.Y + 8;
        _phaseSw.Enabled = hasOut;
        _phaseSw.SetOn(c.InvertPhase);
        _phaseSw.Bounds = new RectF(b.Right - pad - Theme.SwitchW, y0, Theme.SwitchW, ch);

        float y1 = y0 + ch + 8;
        _delaySlider.Enabled = _delayBox.Enabled = hasOut;
        _delaySlider.SetValue(Math.Min(50.0, c.DelayMs));
        _delayBox.Text = $"{c.DelayMs:0.##} ms";

        float distW = 72f;
        _delayDistRect = new RectF(b.Right - pad - distW, y1, distW, ch);
        _delayBox.Bounds = new RectF(_delayDistRect.X - 6 - 72, y1, 72, ch);
        _delaySlider.Bounds = new RectF(b.X + pad + 44, y1, Math.Max(48, _delayBox.Bounds.X - (b.X + pad + 44) - 8), ch);

        float y2 = y1 + ch + 8;
        _ceilDd.Enabled = _lockDd.Enabled = hasOut;
        float half = MathF.Floor((b.W - 2 * pad - 12) / 2f);
        float lblW = 52f;
        _ceilDd.Bounds = new RectF(b.X + pad + lblW, y2, Math.Max(68, half - lblW), ch);
        _lockDd.Bounds = new RectF(b.X + pad + half + 12 + lblW, y2, Math.Max(68, half - lblW), ch);
    }

    public override void Paint(Renderer g)
    {
        var c = _m.OutputCount > 0 ? _m.Channel(false, ClampedOutIdx) : new ChannelDsp();

        if (_grid2x2 || _compactTab == 0) PaintCard0(g, _cardRects[0]);
        if (_grid2x2 || _compactTab == 1) PaintCard1(g, _cardRects[1], c);
        if (_grid2x2 || _compactTab == 2) PaintCard2(g, _cardRects[2], c);
        if (_grid2x2 || _compactTab == 3) PaintCard3(g, _cardRects[3], c);

        PaintChildren(g);
    }

    void PaintCard0(Renderer g, RectF b)
    {
        float pad = 12f;
        Draw.Card(g, b);
        Draw.CardTitle(g, new RectF(b.X + pad, b.Y + 8, b.W - 2 * pad, Theme.ControlHSmall), "Frequenzweiche (Crossover)");
        g.Text("HPF", new RectF(b.X + pad, _hpfTypeDd.Bounds.Y, 34, _hpfTypeDd.Bounds.H), Theme.CaptionStrong, Theme.Teal);
        g.Text("LPF", new RectF(b.X + pad, _lpfTypeDd.Bounds.Y, 34, _lpfTypeDd.Bounds.H), Theme.CaptionStrong, Theme.AccentText);
    }

    void PaintCard1(Renderer g, RectF b, ChannelDsp c)
    {
        float pad = 12f;
        Draw.Card(g, b);
        var bands = GetPeqBands(c);
        Draw.CardTitle(g, new RectF(b.X + pad, b.Y + 8, Math.Max(44, _bandDd.Bounds.X - (b.X + pad) - 4), Theme.ControlHSmall), "PEQ", bands.Count);
        g.Text("Gain", new RectF(b.X + pad, _peqGainSlider.Bounds.Y, 36, _peqGainSlider.Bounds.H), Theme.CaptionStrong, Theme.TextSecondary);
    }

    void PaintCard2(Renderer g, RectF b, ChannelDsp c)
    {
        float pad = 12f;
        Draw.Card(g, b);
        Draw.CardTitle(g, new RectF(b.X + pad, b.Y + 8, Math.Max(60, _rewImportBtn.Bounds.X - (b.X + pad) - 8), Theme.ControlHSmall), "REW-Korrektur");

        if (c.Correction is { } corr)
        {
            var col = corr.Bypassed ? Theme.Orange : Theme.Green;
            float cw = Draw.Chip(g, _rewInfoRect.Right, _rewInfoRect.CY,
                corr.Bypassed ? "Bypass" : $"{corr.Filters.Count} Filter aktiv", col, dot: true, Theme.MicroStrong, alignRight: true);
            g.Text(corr.SourceName, new RectF(_rewInfoRect.X, _rewInfoRect.Y, Math.Max(40, _rewInfoRect.W - cw - 8), _rewInfoRect.H),
                Theme.BodyMedium, Theme.Text);
        }
        else
        {
            g.Text("Keine REW-Datei geladen (PK, LS, HS, LP, HP, NO, AP)", _rewInfoRect, Theme.Caption, Theme.TextSecondary);
        }

        g.Text("Aktiv", new RectF(b.X + pad, _rewActiveSw.Bounds.Y, 38, _rewActiveSw.Bounds.H), Theme.CaptionStrong, Theme.TextSecondary);
        g.Text("Preamp", new RectF(_rewActiveSw.Bounds.Right + 8, _rewPreampSlider.Bounds.Y, 48, _rewPreampSlider.Bounds.H), Theme.CaptionStrong, Theme.TextSecondary);
    }

    void PaintCard3(Renderer g, RectF b, ChannelDsp c)
    {
        float pad = 12f;
        Draw.Card(g, b);
        Draw.CardTitle(g, new RectF(b.X + pad, b.Y + 8, Math.Max(60, _phaseSw.Bounds.X - 78 - (b.X + pad)), Theme.ControlHSmall), "Delay & Schutz");
        g.Text("180° Phase", new RectF(_phaseSw.Bounds.X - 76, _phaseSw.Bounds.Y, 70, _phaseSw.Bounds.H), Theme.CaptionMedium, Theme.TextSecondary, HAlign.Right);

        g.Text("Delay", new RectF(b.X + pad, _delaySlider.Bounds.Y, 42, _delaySlider.Bounds.H), Theme.CaptionStrong, Theme.TextSecondary);
        double cm = c.DelayMs * 34.3;
        string dist = cm >= 100 ? $"{cm / 100.0:0.00} m" : $"{cm:0.0} cm";
        g.Text(dist, _delayDistRect, Theme.CaptionMedium, Theme.TextSecondary, HAlign.Right);

        float half = MathF.Floor((b.W - 2 * pad - 12) / 2f);
        g.Text("Limiter", new RectF(b.X + pad, _ceilDd.Bounds.Y, 50, _ceilDd.Bounds.H), Theme.CaptionStrong, Theme.TextSecondary);
        g.Text("Schutz", new RectF(b.X + pad + half + 12, _lockDd.Bounds.Y, 50, _lockDd.Bounds.H), Theme.CaptionStrong, Theme.TextSecondary);
    }
}
