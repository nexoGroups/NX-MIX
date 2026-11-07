using System.Text.Json;
using System.Text.Json.Serialization;
using NxMix.App.Ui;
using NxMix.Daemon.Persistence;
using NxMix.Ipc;

namespace NxMix.App.Model;

internal sealed record WindowGeometry(double Width, double Height, bool Maximized);

/// <summary>
/// Reine Oberflächen-Einstellungen (gehören zum Arbeitsplatz, niemals zu Mischpult-Snapshots oder zur Audio-Engine):
/// Farbmodus, Maus/Touch, UI-Skalierung, Seitenleiste, Fenstergröße.
/// Liegt als ui.json in %APPDATA%\NX-MIX.
/// </summary>
internal sealed class UiPrefs
{
    public static string DataDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NX-MIX");

    public static bool PersistenceEnabled { get; set; } = true;

    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public Density Density { get; set; } = Density.Mouse;
    public int UiScalePercent { get; set; } = 100;
    public bool SidebarExpanded { get; set; }
    public bool AutoUpdateCheck { get; set; } = true;
    public ReleaseChannel UpdateChannel { get; set; } = ReleaseChannel.Stable;
    public bool UpdateChannelChosen { get; set; }
    public double WindowWidth { get; set; } = NxMix.App.Ui.Theme.DefaultWindowWidth;
    public double WindowHeight { get; set; } = NxMix.App.Ui.Theme.DefaultWindowHeight;
    public bool WindowMaximized { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    static string FilePath => Path.Combine(DataDirectory, "ui.json");
    public float ScaleFactor => ClampScale(UiScalePercent) / 100f;
    public static int ClampScale(int percent) => Math.Clamp(percent, 75, 150);

    public WindowGeometry Window => new(
        WindowWidth > 300 ? WindowWidth : NxMix.App.Ui.Theme.DefaultWindowWidth,
        WindowHeight > 300 ? WindowHeight : NxMix.App.Ui.Theme.DefaultWindowHeight,
        WindowMaximized);

    public void UpdateWindow(WindowGeometry? g)
    {
        if (g == null) return;
        if (g.Width > 300) WindowWidth = Math.Round(g.Width);
        if (g.Height > 300) WindowHeight = Math.Round(g.Height);
        WindowMaximized = g.Maximized;
    }

    public static UiPrefs Load()
    {
        if (!PersistenceEnabled) return new UiPrefs();
        try
        {
            if (File.Exists(FilePath))
            {
                var prefs = JsonSerializer.Deserialize<UiPrefs>(File.ReadAllText(FilePath), s_json) ?? new UiPrefs();
                prefs.UiScalePercent = ClampScale(prefs.UiScalePercent);
                return prefs;
            }
        }
        catch
        {
            ConfigBackupManager.PreserveCorruptFile(FilePath);
        }
        return new UiPrefs();
    }

    public void Save()
    {
        if (!PersistenceEnabled) return;
        try
        {
            Directory.CreateDirectory(DataDirectory);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, s_json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* nicht kritisch */ }
    }
}
