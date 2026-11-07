using System.Text.Json;
using NxMix.Daemon.Persistence;
using NxMix.Ipc;

namespace NxMix.App.Model;

/// <summary>
/// Persistiert benutzerdefinierte Fader-Decks (%APPDATA%\NX-MIX\ui-decks.json).
/// Komplett getrennt von SystemConfig, LiveMixState und Mischpult-Snapshots:
/// UI-Decks sind reine Sichten (Referenzen per ID) und erzeugen niemals eigene Audio-Pfade oder Signal-Duplikate.
/// </summary>
internal static class UiDeckStore
{
    public static string FilePath => Path.Combine(UiPrefs.DataDirectory, "ui-decks.json");

    public static UiDeckCollection Load()
    {
        if (!UiPrefs.PersistenceEnabled) return new UiDeckCollection();
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<UiDeckCollection>(File.ReadAllText(FilePath), IpcJson.PrettyOptions) ?? new UiDeckCollection();
        }
        catch
        {
            ConfigBackupManager.PreserveCorruptFile(FilePath);
        }
        return new UiDeckCollection();
    }

    public static void Save(UiDeckCollection decks)
    {
        if (!UiPrefs.PersistenceEnabled) return;
        try
        {
            Directory.CreateDirectory(UiPrefs.DataDirectory);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(decks, IpcJson.PrettyOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* nicht kritisch */ }
    }
}
