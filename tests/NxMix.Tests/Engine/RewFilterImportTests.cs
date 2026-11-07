using NxMix.Engine;

namespace NxMix.Tests.Engine;

public sealed class RewFilterImportTests
{
    [Fact]
    public void Parses_PK_Shelves_Pass_Notch_And_AllPass_With_Boost_Headroom_Preview()
    {
        const string rewText = """
            Filter Settings file

            Room EQ V5,31
            Dated: 07.11.2026 14:15:00

            Notes: Nahfeldmonitor Links

            Equaliser: Generic
            Filter  1: ON  PK       Fc    62.5 Hz  Gain  -5.40 dB  Q  4.200
            Filter  2: ON  PK       Fc   420.0 Hz  Gain   3.50 dB  Q  2.100
            Filter  3: ON  LS Q     Fc    95.0 Hz  Gain   2.00 dB  Q  0.750
            Filter  4: ON  HS 12dB  Fc  8500.0 Hz  Gain  -1.80 dB
            Filter  5: ON  HP Q     Fc    35.0 Hz  Q  0.707
            Filter  6: ON  LP       Fc 18500.0 Hz
            Filter  7: ON  NO       Fc    50.0 Hz
            Filter  8: ON  AP       Fc  1200.0 Hz  Q  1.400
            Filter  9: OFF PK       Fc  2000.0 Hz  Gain   6.00 dB  Q  1.000
            Filter 10: ON  None
            """;

        using var reader = new StringReader(rewText);
        var preview = RewFilterParser.ParsePreview(reader, "monitor-left.txt", 48000);

        Assert.Equal("monitor-left.txt", preview.SourceName);
        Assert.Equal(8, preview.Filters.Count);
        Assert.Equal(2, preview.SkippedCount);

        Assert.Equal(FilterType.Peak, preview.Filters[0].Type);
        Assert.Equal(-5.40, preview.Filters[0].GainDb, 2);

        Assert.Equal(FilterType.LowShelf, preview.Filters[2].Type);
        Assert.Equal(2.00, preview.Filters[2].GainDb, 2);
        Assert.Equal(0.75, preview.Filters[2].Q, 2);

        Assert.Equal(FilterType.HighShelf, preview.Filters[3].Type);
        Assert.Equal(-1.80, preview.Filters[3].GainDb, 2);

        Assert.Equal(FilterType.HighPass, preview.Filters[4].Type);
        Assert.Equal(2, preview.Filters[4].Order);

        Assert.Equal(FilterType.LowPass, preview.Filters[5].Type);
        Assert.Equal(FilterType.Notch, preview.Filters[6].Type);
        Assert.Equal(FilterType.AllPass, preview.Filters[7].Type);

        // Wegen +3.5 dB Peak und +2.0 dB Low-Shelf liegt ein positiver Boost vor -> negative Preamp-Empfehlung
        Assert.True(preview.MaxBoostDb > 3.0);
        Assert.True(preview.RecommendedPreampDb < -3.0);

        var corr = RewFilterParser.BuildOutputCorrection(preview, applyHeadroomTrim: true);
        Assert.Equal(preview.RecommendedPreampDb, corr.PreampDb);
        Assert.False(corr.Bypassed);
    }

    [Fact]
    public void Rejects_EqualizerApo_And_Binary_Mdat_With_Clear_German_Explanation()
    {
        const string apoText = """
            Preamp: -4.2 dB
            Filter 1: ON PK Fc 100 Hz Gain -3.0 dB Q 2.0
            """;
        using var apoReader = new StringReader(apoText);
        var apoEx = Assert.Throws<FormatException>(() => RewFilterParser.ParsePreview(apoReader, "apo.txt", 48000));
        Assert.Contains("EqualizerAPO", apoEx.Message);

        string tempPath = Path.Combine(Path.GetTempPath(), $"nxmix-mdat-{Guid.NewGuid():N}.mdat");
        try
        {
            File.WriteAllBytes(tempPath, [0x50, 0x4B, 0x03, 0x04, 0x00, 0x01, 0x02]);
            var binEx = Assert.Throws<FormatException>(() => RewFilterParser.ReadPreview(tempPath, 48000));
            Assert.Contains(".mdat", binEx.Message);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }
}
