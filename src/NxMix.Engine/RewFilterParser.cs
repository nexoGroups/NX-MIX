using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace NxMix.Engine;

/// <summary>Vorschau-Ergebnis eines REW-Filterimports inklusive berechnetem Maximal-Boost und Headroom-Empfehlung.</summary>
public sealed record RewImportPreview(
    string SourceName,
    IReadOnlyList<FilterSpec> Filters,
    int SkippedCount,
    double MaxBoostDb,
    double RecommendedPreampDb,
    IReadOnlyList<string> TypeSummary);

/// <summary>Erweiterter REW-Generic-Filter-Parser für Ausgangs-Raum- und Lautsprecherkorrekturen.
/// Unterstützt PK, LS / LS Q / LS 6dB / LS 12dB / LSC, HS / HS Q / HS 6dB / HS 12dB / HSC, LP / LP Q / LP1, HP / HP Q / HP1, NO und AP.</summary>
public static partial class RewFilterParser
{
    public const int MaxActiveFilters = 24;

    [GeneratedRegex(@"^Filter\s*(?:[0-9]+)?\s*:\s*(ON|OFF)\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FilterHeaderRegex();

    [GeneratedRegex(@"\bFc\s+([+\-0-9.,eE]+)\s*Hz", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FcRegex();

    [GeneratedRegex(@"\bGain\s+([+\-0-9.,eE]+)\s*dB", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GainRegex();

    [GeneratedRegex(@"\bQ\s+([+\-0-9.,eE]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QRegex();

    public static RewImportPreview ReadPreview(string path, int sampleRate)
    {
        using var file = File.OpenRead(path);
        if (file.Length == 0)
            throw new FormatException("Die ausgewählte Datei ist leer.");
        if (file.Length > 128 * 1024)
            throw new FormatException("Die Filterdatei ist größer als 128 KB. Bitte keine REW-Messdatei (.mdat), sondern „Export filter settings as text“ wählen.");

        // Binärdatei- / .mdat-Prüfung (erste 256 Bytes)
        Span<byte> headBytes = stackalloc byte[(int)Math.Min(256, file.Length)];
        int read = file.Read(headBytes);
        headBytes = headBytes[..read];
        if ((read >= 4 && headBytes[0] == (byte)'P' && headBytes[1] == (byte)'K' && headBytes[2] == 3 && headBytes[3] == 4) ||
            headBytes.IndexOf((byte)0) >= 0)
        {
            throw new FormatException("Binärdatei erkannt (z. B. REW-Messprojekt .mdat). Bitte in REW im EQ-Fenster „Export filter settings as text“ wählen.");
        }

        file.Position = 0;
        using var reader = new StreamReader(file);
        return ParsePreview(reader, Path.GetFileName(path), sampleRate);
    }

    public static OutputCorrection BuildOutputCorrection(RewImportPreview preview, bool applyHeadroomTrim)
    {
        double preamp = applyHeadroomTrim ? preview.RecommendedPreampDb : 0.0;
        return new OutputCorrection(preview.SourceName, preview.Filters, Bypassed: false, PreampDb: preamp);
    }

    public static OutputCorrection Read(string path, int sampleRate, bool applyHeadroomTrim = false)
    {
        var preview = ReadPreview(path, sampleRate);
        return BuildOutputCorrection(preview, applyHeadroomTrim);
    }

    public static RewImportPreview ParsePreview(TextReader reader, string sourceName, int sampleRate)
    {
        int sr = sampleRate > 0 ? sampleRate : 48000;
        int lineNumber = 0;
        bool header = false;
        bool generic = false;
        bool inFilters = false;
        int skipped = 0;
        var filters = new List<FilterSpec>();
        var typeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        while (reader.ReadLine() is { } raw)
        {
            lineNumber++;
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('*')) continue;

            if (!header)
            {
                if (line.StartsWith("Preamp:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("GraphicEQ:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Channel:", StringComparison.OrdinalIgnoreCase))
                {
                    throw new FormatException("EqualizerAPO-Format erkannt. Bitte in REW „Export filter settings as text“ (nicht „formatted for EqualizerAPO“) mit Equaliser „Generic“ exportieren.");
                }
                if (!line.Equals("Filter Settings file", StringComparison.OrdinalIgnoreCase))
                    throw new FormatException("Ungültiger Dateikopf. Bitte in REW den Export „Export filter settings as text“ wählen.");
                header = true;
                continue;
            }

            if (line.StartsWith("Equaliser:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Equalizer:", StringComparison.OrdinalIgnoreCase))
            {
                string eqName = line[(line.IndexOf(':') + 1)..].Trim();
                if (!eqName.StartsWith("Generic", StringComparison.OrdinalIgnoreCase))
                    throw new FormatException($"Equaliser „{eqName}“ wird nicht unterstützt. Bitte in REW den Equaliser „Generic“ auswählen.");
                generic = true;
                continue;
            }

            var match = FilterHeaderRegex().Match(line);
            if (!match.Success)
            {
                if (inFilters || line.StartsWith("Filter ", StringComparison.OrdinalIgnoreCase))
                    throw new FormatException($"Zeile {lineNumber}: Ungültige Filterzeile.");
                continue; // REW-Metadaten (Dated, Notes, RoomSim usw.)
            }

            inFilters = true;
            bool isOn = match.Groups[1].Value.Equals("ON", StringComparison.OrdinalIgnoreCase);
            string rest = match.Groups[2].Value.Trim();

            if (!isOn || rest.StartsWith("None", StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }

            var (spec, badge) = ParseFilterBody(rest, lineNumber, sr);
            if (filters.Count >= MaxActiveFilters)
                throw new FormatException($"Die Datei enthält mehr als {MaxActiveFilters} aktive Filter.");

            filters.Add(spec);
            typeCounts[badge] = typeCounts.TryGetValue(badge, out int c) ? c + 1 : 1;
        }

        if (!header)
            throw new FormatException("Die Datei enthält keine REW-Filtereinstellungen.");
        if (!generic)
            throw new FormatException("Kein REW-„Generic“-Equaliser erkannt. Bitte in REW mit Equaliser „Generic“ exportieren.");
        if (filters.Count == 0)
            throw new FormatException("Die Datei enthält keine aktiven Filter (alle Filter stehen auf OFF oder None).");

        double maxBoost = ComputeMaxBoostDb(filters, sr);
        double recPreamp = maxBoost > 0.1 ? -Math.Ceiling(maxBoost * 10.0) / 10.0 : 0.0;
        var summary = typeCounts.Select(kv => $"{kv.Value}× {kv.Key}").ToArray();

        return new RewImportPreview(
            string.IsNullOrWhiteSpace(sourceName) ? "REW-Import" : sourceName,
            filters.ToArray(),
            skipped,
            Math.Round(maxBoost, 2),
            recPreamp,
            summary);
    }

    private static (FilterSpec Spec, string Badge) ParseFilterBody(string body, int lineNumber, int sampleRate)
    {
        // Fc extrahieren — markiert gleichzeitig das Ende des Filtertyp-Tokens (z. B. "LS Q", "LS 12dB", "PK")
        var fcMatch = FcRegex().Match(body);
        if (!fcMatch.Success || !TryParseNumber(fcMatch.Groups[1].Value, out double hz))
            throw new FormatException($"Zeile {lineNumber}: Grenz-/Mittenfrequenz (Fc ... Hz) fehlt oder ist ungültig.");

        double maxHz = FilterDesign.MaxCorrectionFrequencyHz(sampleRate);
        if (hz < 10.0 || hz > maxHz)
            throw new FormatException($"Zeile {lineNumber}: Frequenz {hz:0.#} Hz liegt außerhalb des zulässigen Bereichs (10 .. {maxHz:0} Hz bei {sampleRate} Hz).");

        string rawType = NormalizeSpaces(body[..fcMatch.Index].Trim());
        string tail = body[fcMatch.Index..];

        bool hasGain = GainRegex().Match(tail) is { Success: true } gm && TryParseNumber(gm.Groups[1].Value, out double parsedGain)
            ? true : (parsedGain = 0, false).Item2;
        bool hasQ = QRegex().Match(tail) is { Success: true } qm && TryParseNumber(qm.Groups[1].Value, out double parsedQ)
            ? true : (parsedQ = 0.7071067811865476, false).Item2;

        if (hasGain && (parsedGain < FilterDesign.MinCorrectionGainDb || parsedGain > FilterDesign.MaxFilterGainDb))
            throw new FormatException($"Zeile {lineNumber}: Gain {parsedGain:0.##} dB liegt außerhalb des gültigen Bereichs.");
        if (hasQ && (parsedQ < 0.05 || parsedQ > 80.0))
            throw new FormatException($"Zeile {lineNumber}: Güte Q={parsedQ:0.###} liegt außerhalb des gültigen Bereichs (0.05 .. 80).");

        switch (rawType.ToUpperInvariant())
        {
            case "PK":
            case "PEAK":
                if (!hasGain || !hasQ)
                    throw new FormatException($"Zeile {lineNumber}: Peak-Filter (PK) benötigt Fc, Gain und Q.");
                return (new FilterSpec(FilterType.Peak, hz, parsedQ, parsedGain), "PK");

            case "LS":
            case "LS Q":
            case "LSC":
                if (!hasGain)
                    throw new FormatException($"Zeile {lineNumber}: Low-Shelf-Filter ({rawType}) benötigt einen Gain-Wert in dB.");
                return (new FilterSpec(FilterType.LowShelf, hz, hasQ ? parsedQ : 0.7071067811865476, parsedGain), "LS");

            case "LS 6DB":
                if (!hasGain)
                    throw new FormatException($"Zeile {lineNumber}: Low-Shelf-Filter ({rawType}) benötigt einen Gain-Wert in dB.");
                return (new FilterSpec(FilterType.LowShelf, hz, 0.5, parsedGain), "LS");

            case "LS 12DB":
                if (!hasGain)
                    throw new FormatException($"Zeile {lineNumber}: Low-Shelf-Filter ({rawType}) benötigt einen Gain-Wert in dB.");
                return (new FilterSpec(FilterType.LowShelf, hz, 0.7071067811865476, parsedGain), "LS");

            case "HS":
            case "HS Q":
            case "HSC":
                if (!hasGain)
                    throw new FormatException($"Zeile {lineNumber}: High-Shelf-Filter ({rawType}) benötigt einen Gain-Wert in dB.");
                return (new FilterSpec(FilterType.HighShelf, hz, hasQ ? parsedQ : 0.7071067811865476, parsedGain), "HS");

            case "HS 6DB":
                if (!hasGain)
                    throw new FormatException($"Zeile {lineNumber}: High-Shelf-Filter ({rawType}) benötigt einen Gain-Wert in dB.");
                return (new FilterSpec(FilterType.HighShelf, hz, 0.5, parsedGain), "HS");

            case "HS 12DB":
                if (!hasGain)
                    throw new FormatException($"Zeile {lineNumber}: High-Shelf-Filter ({rawType}) benötigt einen Gain-Wert in dB.");
                return (new FilterSpec(FilterType.HighShelf, hz, 0.7071067811865476, parsedGain), "HS");

            case "LP":
            case "LP Q":
                return (new FilterSpec(FilterType.LowPass, hz, hasQ ? parsedQ : 0.7071067811865476, 0, Order: 2), "LP");

            case "LP1":
                return (new FilterSpec(FilterType.LowPass, hz, 0.7071067811865476, 0, Order: 1), "LP");

            case "HP":
            case "HP Q":
                return (new FilterSpec(FilterType.HighPass, hz, hasQ ? parsedQ : 0.7071067811865476, 0, Order: 2), "HP");

            case "HP1":
                return (new FilterSpec(FilterType.HighPass, hz, 0.7071067811865476, 0, Order: 1), "HP");

            case "NO":
            case "NOTCH":
                return (new FilterSpec(FilterType.Notch, hz, hasQ ? parsedQ : 10.0, 0), "NO");

            case "AP":
            case "ALLPASS":
                return (new FilterSpec(FilterType.AllPass, hz, hasQ ? parsedQ : 0.7071067811865476, 0), "AP");

            default:
                throw new FormatException(
                    $"Zeile {lineNumber}: Aktiver Filtertyp „{rawType}“ wird nicht unterstützt. " +
                    "Unterstützt werden: PK, LS, LS Q, LS 6dB, LS 12dB, HS, HS Q, HS 6dB, HS 12dB, LP, LP Q, LP1, HP, HP Q, HP1, NO und AP.");
        }
    }

    public static double ComputeMaxBoostDb(IReadOnlyList<FilterSpec> filters, int sampleRate)
    {
        if (filters.Count == 0) return 0.0;
        int sr = sampleRate > 0 ? sampleRate : 48000;
        var dummy = new ChannelDsp { Correction = new OutputCorrection("preview", filters, Bypassed: false) };
        var sections = FilterDesign.DesignChannel(dummy, sr, input: false);
        if (sections.Count == 0) return 0.0;

        double maxDb = 0.0;
        const int points = 512;
        double minHz = 20.0;
        double maxHz = Math.Min(20000.0, 0.48 * sr);
        double logSpan = Math.Log(maxHz / minHz);

        for (int i = 0; i < points; i++)
        {
            double f = minHz * Math.Exp(logSpan * i / (points - 1));
            Complex h = Complex.One;
            foreach (var sec in sections)
                h *= sec.Response(f, sr);
            double db = 20.0 * Math.Log10(Math.Max(1e-12, h.Magnitude));
            if (db > maxDb) maxDb = db;
        }
        return maxDb;
    }

    private static string NormalizeSpaces(string value)
    {
        if (value.Length == 0) return value;
        return string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool TryParseNumber(string raw, out double result)
    {
        // REW schreibt je nach Gebietsschema gelegentlich Komma statt Punkt (z. B. "120,0" oder "1,25")
        string normalized = raw.Trim().Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out result) && double.IsFinite(result);
    }
}
