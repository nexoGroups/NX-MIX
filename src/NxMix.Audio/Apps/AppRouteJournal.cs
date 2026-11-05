using System.Text.Json;
using NxMix.Audio.Interop;

namespace NxMix.Audio.Apps;

/// <summary>Ein Eintrag im Journal: dieses Programm (Exe) ist von uns auf das Parkplatz-Geraet umgeleitet. Previous = der VORHER gesetzte
/// Pro-App-Wert je Rolle (Console, Multimedia, Communications; null = kein Wert, also System-Standard).</summary>
internal sealed class RouteEntry
{
    public string Exe { get; set; } = "";
    public string Parking { get; set; } = "";
    public string?[] Previous { get; set; } = new string?[3];
    public int OwnerPid { get; set; }
    public string? SetAtUtc { get; set; }
    /// <summary>true: beim Zuruecksetzen lief kein Prozess dieser Exe (Windows laesst sich nur ueber eine laufende PID umstellen). Wird beim naechsten Mal nachgeholt.</summary>
    public bool Pending { get; set; }

    public RouteEntry Clone() => new()
    {
        Exe = Exe, Parking = Parking, Previous = (string?[])Previous.Clone(), OwnerPid = OwnerPid, SetAtUtc = SetAtUtc, Pending = Pending,
    };
}

/// <summary>Journal der Umleitungen (%APPDATA%\NX-MIX\app-routes.json). Eintrag VOR dem Umleiten schreiben, NACH dem Zuruecksetzen austragen.
/// Toleriert fehlende/kaputte Dateien. Schreiben atomar (temp + move). Wirft nie bei Lesefehlern; Schreibfehler werden gemeldet (Exception).</summary>
internal sealed class AppRouteJournal
{
    private sealed class FileModel
    {
        public int Version { get; set; } = 1;
        public List<RouteEntry> Routes { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly object _gate = new();

    public AppRouteJournal(string? path = null) => Path = path ?? DefaultPath;

    public string Path { get; }

    /// <summary>Nur fuer Tests: ersetzt den Standardpfad (%APPDATA%\NX-MIX\app-routes.json).</summary>
    internal static volatile string? PathOverride;

    public static string DefaultPath => PathOverride ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NX-MIX", "app-routes.json");

    public List<RouteEntry> Load()
    {
        lock (_gate) return LoadCore();
    }

    private List<RouteEntry> LoadCore()
    {
        try
        {
            if (!File.Exists(Path)) return new List<RouteEntry>();
            string text = File.ReadAllText(Path);
            if (string.IsNullOrWhiteSpace(text)) return new List<RouteEntry>();
            FileModel? m = JsonSerializer.Deserialize<FileModel>(text, Json);
            var list = new List<RouteEntry>();
            foreach (RouteEntry e in m?.Routes ?? new List<RouteEntry>())
            {
                if (string.IsNullOrWhiteSpace(e.Exe)) continue;
                e.Exe = ProcessSelector.NormalizeExe(e.Exe);
                e.Parking ??= "";
                var prev = new string?[3];
                for (int i = 0; i < 3 && e.Previous != null && i < e.Previous.Length; i++) prev[i] = string.IsNullOrEmpty(e.Previous[i]) ? null : e.Previous[i];
                e.Previous = prev;
                list.Add(e);
            }
            return list;
        }
        catch
        {
            try { File.Move(Path, Path + ".bad", true); } catch { /* egal */ }
            return new List<RouteEntry>();
        }
    }

    private void SaveCore(List<RouteEntry> routes)
    {
        string? dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (routes.Count == 0)
        {
            // Nichts mehr zu merken: Datei leeren statt zu loeschen (kein Rennen mit parallelem Lesen), aber klein halten
            File.WriteAllText(Path, JsonSerializer.Serialize(new FileModel(), Json));
            return;
        }
        string tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new FileModel { Routes = routes }, Json));
        File.Move(tmp, Path, true);
    }

    public RouteEntry? Find(string exe)
    {
        string key = ProcessSelector.NormalizeExe(exe);
        lock (_gate) return LoadCore().FirstOrDefault(e => e.Exe == key);
    }

    /// <summary>Traegt den Eintrag ein oder ersetzt ihn (gleiche Exe).</summary>
    public void Upsert(RouteEntry entry)
    {
        entry.Exe = ProcessSelector.NormalizeExe(entry.Exe);
        lock (_gate)
        {
            List<RouteEntry> all = LoadCore();
            all.RemoveAll(e => e.Exe == entry.Exe);
            all.Add(entry);
            SaveCore(all);
        }
    }

    public bool Remove(string exe)
    {
        string key = ProcessSelector.NormalizeExe(exe);
        lock (_gate)
        {
            List<RouteEntry> all = LoadCore();
            if (all.RemoveAll(e => e.Exe == key) == 0) return false;
            SaveCore(all);
            return true;
        }
    }
}
