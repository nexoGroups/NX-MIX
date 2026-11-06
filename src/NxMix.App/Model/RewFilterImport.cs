using System.Globalization;
using System.Text.RegularExpressions;
using NxMix.Engine;

namespace NxMix.App.Model;

internal static partial class RewFilterImport
{
    [GeneratedRegex(@"^Filter\s*(?:[0-9]+)?\s*:\s*(ON|OFF)\s+(\w+)(.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FilterLine();

    [GeneratedRegex(@"^\s+Fc\s+([^\s]+)\s*Hz\s+Gain\s+([^\s]+)\s*dB\s+Q\s+([^\s]+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PeakParameters();

    public static OutputCorrection Read(string path, int sampleRate)
    {
        using var file = File.OpenRead(path);
        if (file.Length > 64 * 1024) throw new FormatException("Die Filterdatei ist größer als 64 KB.");
        using var reader = new StreamReader(file);
        return new OutputCorrection(Path.GetFileName(path), Parse(reader, sampleRate));
    }

    internal static IReadOnlyList<FilterSpec> Parse(TextReader reader, int sampleRate)
    {
        int lineNumber = 0;
        bool header = false, generic = false, inFilters = false;
        var filters = new List<FilterSpec>();

        while (reader.ReadLine() is { } raw)
        {
            lineNumber++;
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (!header)
            {
                if (!line.Equals("Filter Settings file", StringComparison.OrdinalIgnoreCase))
                    throw new FormatException("Bitte in REW den Filterexport „Export filter settings as text“ wählen.");
                header = true;
                continue;
            }
            if (line.StartsWith("Equaliser:", StringComparison.OrdinalIgnoreCase))
            {
                if (!line[10..].Trim().Equals("Generic", StringComparison.OrdinalIgnoreCase))
                    throw new FormatException("Bitte die Filter in REW mit dem Equaliser „Generic“ exportieren.");
                generic = true;
                continue;
            }

            var match = FilterLine().Match(line);
            if (!match.Success)
            {
                if (inFilters || line.StartsWith("Filter ", StringComparison.OrdinalIgnoreCase))
                    throw new FormatException($"Zeile {lineNumber}: Ungültige Filterzeile.");
                continue; // REW-Metadaten vor den Filtern, z. B. Messname oder Zielpegel
            }

            inFilters = true;
            string type = match.Groups[2].Value;
            if (match.Groups[1].Value.Equals("OFF", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("None", StringComparison.OrdinalIgnoreCase)) continue;
            if (!type.Equals("PK", StringComparison.OrdinalIgnoreCase))
                throw new FormatException($"Zeile {lineNumber}: Aktiver Filtertyp „{type}“ wird nicht unterstützt. Erwartet wird PK.");

            var values = PeakParameters().Match(match.Groups[3].Value);
            if (!values.Success || !Number(values.Groups[1].Value, out double hz) ||
                !Number(values.Groups[2].Value, out double db) || !Number(values.Groups[3].Value, out double q))
                throw new FormatException($"Zeile {lineNumber}: Frequenz, Gain oder Q fehlt oder ist ungültig.");
            if (hz < 10 || hz > FilterDesign.MaxCorrectionFrequencyHz(sampleRate) ||
                db < FilterDesign.MinCorrectionGainDb || db > 30 || q < 0.1 || q > 50)
                throw new FormatException($"Zeile {lineNumber}: Filterwerte liegen außerhalb des REW-Generic-Bereichs bei {sampleRate} Hz.");
            if (filters.Count == 20) throw new FormatException("Die Datei enthält mehr als 20 aktive Filter.");
            filters.Add(new FilterSpec(FilterType.Peak, hz, q, db));
        }

        if (!generic || filters.Count == 0)
            throw new FormatException("Die Datei enthält keinen aktiven REW-Generic-Filter (PK).");
        return filters.ToArray();
    }

    private static bool Number(string value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) && double.IsFinite(result);
}
