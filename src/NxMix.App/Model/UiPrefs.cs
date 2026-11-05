using System.Text.Json;
using System.Text.Json.Serialization;
using NxMix.App.Ui;

namespace NxMix.App.Model;

/// <summary>
/// Darstellung (gehoert zum Geraet, nicht zum Projekt): Farbmodus, Maus/Touch, Seitenleiste.
/// Liegt als ui.json neben project.json und wird bei jeder Aenderung sofort geschrieben.
/// </summary>
internal sealed class UiPrefs
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public Density Density { get; set; } = Density.Mouse;
    /// <summary>Vorgabe wie bei UniFi: schmale Symbolleiste, Texte als Tooltip.</summary>
    public bool SidebarExpanded { get; set; }
    /// <summary>Beim Start im Hintergrund nach einer neuen Version suchen (installiert wird nur auf Klick).</summary>
    public bool AutoUpdateCheck { get; set; } = true;

    static readonly JsonSerializerOptions s_json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    static string FilePath => Path.Combine(ProjectStore.Directory, "ui.json");

    public static UiPrefs Load()
    {
        if (!ProjectStore.Enabled) return new UiPrefs();
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<UiPrefs>(File.ReadAllText(FilePath), s_json) ?? new UiPrefs();
        }
        catch { /* kaputte Datei: Vorgaben */ }
        return new UiPrefs();
    }

    public void Save()
    {
        if (!ProjectStore.Enabled) return;
        try
        {
            System.IO.Directory.CreateDirectory(ProjectStore.Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, s_json));
        }
        catch { /* nicht kritisch */ }
    }
}
