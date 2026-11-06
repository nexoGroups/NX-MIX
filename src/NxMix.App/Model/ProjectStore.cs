using System.Text.Json;
using System.Text.Json.Serialization;
using NxMix.Audio;
using NxMix.Engine;

namespace NxMix.App.Model;

// ---- Dateiformat (JSON) --------------------------------------------------------------------
// -unendlich / NaN werden als null geschrieben (Matrix: null = nicht verbunden; GainDb: null = -unendlich).

internal sealed class ChannelDto
{
    public string Name { get; set; } = "";
    public double? GainDb { get; set; } = 0;
    public bool Mute { get; set; }
    public bool Solo { get; set; }
    public bool InvertPhase { get; set; }
    public double DelayMs { get; set; }
    public double CeilingDbfs { get; set; } = -1;
    public double MaxGainDb { get; set; } = 12;
    public List<FilterSpec>? Filters { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OutputCorrection? Correction { get; set; }
}

internal sealed class BindingDto
{
    public string DeviceId { get; set; } = "";
    public string? DeviceName { get; set; }
    public DeviceFlow Flow { get; set; }
    public int DeviceFirstChannel { get; set; }
    public int EngineFirstChannel { get; set; }
    public int Count { get; set; } = 1;
    public bool Loopback { get; set; }
}

/// <summary>Ein App-Link: Programm (Exe-Name) an Eingang Channel (0-basiert) und folgende (Count 1 = mono, 2 = Stereo).</summary>
internal sealed class AppLinkDto
{
    public string ExeName { get; set; } = "";
    public int Channel { get; set; }
    public int Count { get; set; } = 2;
    public string? DisplayName { get; set; }
}

internal sealed class WindowDto
{
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

internal sealed class ProjectDto
{
    public int Version { get; set; } = 1;
    public int SampleRate { get; set; } = AppModel.DefaultSampleRate;
    public int BlockFrames { get; set; } = AppModel.DefaultBlockFrames;
    public bool Exclusive { get; set; }
    public double MasterGainDb { get; set; }
    public List<ChannelDto> Inputs { get; set; } = new();
    public List<ChannelDto> Outputs { get; set; } = new();
    public List<List<double?>> Matrix { get; set; } = new();
    public List<BindingDto> Bindings { get; set; } = new();
    /// <summary>Fehlt in aelteren Dateien (= keine Links).</summary>
    public List<AppLinkDto>? AppLinks { get; set; }
    /// <summary>Hauptschalter fuer App-Links; fehlt in aelteren Dateien (= an).</summary>
    public bool AppLinksEnabled { get; set; } = true;
    /// <summary>Render-Geraet, auf das Programme umgeleitet werden (stilles virtuelles Geraet).</summary>
    public string? ParkingDeviceId { get; set; }
    public string? ParkingDeviceName { get; set; }
    public WindowDto? Window { get; set; }
}

/// <summary>Speichern/Laden des Projekts. Standard: %APPDATA%\NX-MIX\project.json (mit --data-dir umlenkbar).</summary>
internal static class ProjectStore
{
    public static string Directory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NX-MIX");
    public static string FilePath => Path.Combine(Directory, "project.json");
    /// <summary>False: nie auf die Platte schreiben/lesen (Screenshots, Selbsttest).</summary>
    public static bool Enabled { get; set; } = true;

    static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    // ---- Abbild: Modell <-> DTO --------------------------------------------------------------
    static double? Fin(double v) => double.IsNaN(v) || double.IsInfinity(v) ? null : v;

    static ChannelDto ToDto(ChannelDsp c) => new()
    {
        Name = c.Name, GainDb = Fin(c.GainDb), Mute = c.Mute, Solo = c.Solo, InvertPhase = c.InvertPhase,
        DelayMs = c.DelayMs, CeilingDbfs = c.CeilingDbfs, MaxGainDb = c.MaxGainDb, Filters = c.Filters.ToList(),
        Correction = c.Correction,
    };

    static ChannelDsp FromDto(ChannelDto d) => new()
    {
        Name = d.Name ?? "",
        GainDb = d.GainDb ?? double.NegativeInfinity,
        Mute = d.Mute, Solo = d.Solo, InvertPhase = d.InvertPhase,
        DelayMs = d.DelayMs, CeilingDbfs = d.CeilingDbfs, MaxGainDb = d.MaxGainDb,
        Filters = (IReadOnlyList<FilterSpec>?)d.Filters ?? Array.Empty<FilterSpec>(),
        Correction = d.Correction,
    };

    public static ProjectDto ToDto(AppModel m, WindowDto? window)
    {
        var s = m.Settings;
        return new ProjectDto
        {
            Version = s.Outputs.Any(c => c.Correction != null) ? 2 : 1,
            SampleRate = s.SampleRate,
            BlockFrames = m.BlockFrames,
            Exclusive = m.Exclusive,
            MasterGainDb = s.MasterGainDb,
            Inputs = s.Inputs.Select(ToDto).ToList(),
            Outputs = s.Outputs.Select(ToDto).ToList(),
            Matrix = s.MatrixDb.Select(r => r.Select(Fin).ToList()).ToList(),
            Bindings = m.Bindings.Select(b => new BindingDto
            {
                DeviceId = b.DeviceId, DeviceName = m.FindDevice(b.DeviceId)?.Name, Flow = b.Flow, DeviceFirstChannel = b.DeviceFirstChannel,
                EngineFirstChannel = b.EngineFirstChannel, Count = b.Count, Loopback = b.Loopback,
            }).ToList(),
            AppLinks = m.AppLinks.Select(l => new AppLinkDto { ExeName = l.ExeName, Channel = l.FirstChannel, Count = l.Count, DisplayName = l.DisplayName }).ToList(),
            AppLinksEnabled = m.AppLinksEnabled,
            ParkingDeviceId = m.ParkingDeviceId,
            ParkingDeviceName = m.ParkingDeviceName,
            Window = window,
        };
    }

    /// <summary>App-Links aus der Datei (fehlendes Feld = keine Links, Schalter an).</summary>
    public static AppLinkSetup AppLinksFromDto(ProjectDto d) => new(
        (d.AppLinks ?? new List<AppLinkDto>()).Select(l => new AppLink(l.ExeName ?? "", Math.Max(0, l.Channel), Math.Clamp(l.Count, 1, 2), l.DisplayName)).ToList(),
        d.AppLinksEnabled, string.IsNullOrWhiteSpace(d.ParkingDeviceId) ? null : d.ParkingDeviceId, d.ParkingDeviceName);

    public static (MixerSettings Settings, List<DeviceBinding> Bindings) FromDto(ProjectDto d)
    {
        var ins = d.Inputs.Select(FromDto).ToArray();
        var outs = d.Outputs.Select(FromDto).ToArray();
        var matrix = new double[ins.Length][];
        for (int i = 0; i < ins.Length; i++)
        {
            matrix[i] = new double[outs.Length];
            for (int j = 0; j < outs.Length; j++)
            {
                double? v = i < d.Matrix.Count && j < d.Matrix[i].Count ? d.Matrix[i][j] : null;
                matrix[i][j] = v ?? double.NegativeInfinity;
            }
        }
        var settings = new MixerSettings
        {
            SampleRate = d.SampleRate > 0 ? d.SampleRate : AppModel.DefaultSampleRate,
            Inputs = ins, Outputs = outs, MatrixDb = matrix, MasterGainDb = d.MasterGainDb,
        };
        var bindings = d.Bindings.Select(b => new DeviceBinding(b.DeviceId ?? "", b.Flow, Math.Max(0, b.DeviceFirstChannel),
            Math.Max(0, b.EngineFirstChannel), Math.Max(1, b.Count), b.Loopback)).ToList();
        return (settings, bindings);
    }

    // ---- Datei -------------------------------------------------------------------------------
    public static void WriteFile(string path, ProjectDto dto)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(dto, s_json));
        File.Move(tmp, path, true);
    }

    public static ProjectDto ReadFile(string path)
    {
        var dto = JsonSerializer.Deserialize<ProjectDto>(File.ReadAllText(path), s_json) ?? throw new InvalidDataException("Leere Datei");
        if (dto.Version > 2) throw new InvalidDataException("Die Datei stammt von einer neueren NX-MIX-Version.");
        return dto;
    }

    /// <summary>Liest das automatisch gespeicherte Projekt. Kaputte Datei -> .bad-Kopie und null.</summary>
    public static ProjectDto? TryLoadAuto()
    {
        if (!Enabled || !File.Exists(FilePath)) return null;
        try { return ReadFile(FilePath); }
        catch
        {
            try { File.Copy(FilePath, FilePath + ".bad", true); } catch { }
            return null;
        }
    }

    public static bool TrySaveAuto(ProjectDto dto)
    {
        if (!Enabled) return false;
        try { WriteFile(FilePath, dto); return true; }
        catch { return false; }
    }
}
