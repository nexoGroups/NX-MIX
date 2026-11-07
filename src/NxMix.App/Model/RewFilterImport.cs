using NxMix.Engine;

namespace NxMix.App.Model;

/// <summary>Brücke zum erweiterten REW-Generic-Filter-Parser in NxMix.Engine (inkl. Vorschau &amp; Headroom-Trim).</summary>
internal static class RewFilterImport
{
    public static RewImportPreview ReadPreview(string path, int sampleRate) =>
        RewFilterParser.ReadPreview(path, sampleRate);

    public static OutputCorrection Read(string path, int sampleRate, bool applyHeadroomTrim = false) =>
        RewFilterParser.Read(path, sampleRate, applyHeadroomTrim);

    public static OutputCorrection Build(RewImportPreview preview, bool applyHeadroomTrim) =>
        RewFilterParser.BuildOutputCorrection(preview, applyHeadroomTrim);
}
