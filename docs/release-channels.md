# NX-MIX – Release-Kanäle, Auto-Update & GitHub-Veröffentlichung

NX-MIX verwendet ein zweistufiges Release- und Auto-Update-System auf Basis von **Velopack** und **GitHub Releases**:

| Eigenschaft | **Stable / Release** | **Beta / Prerelease** |
|---|---|---|
| **Einsatzzweck** | Dauerhafter Live-, Studio- & Streaming-Betrieb | Vorabtest neuer Funktionen & früher Bugfixes |
| **Versionsschema** | Reine SemVer (`v2.0.0`, `v2.1.0`) | SemVer mit Suffix (`v2.1.0-beta.1`, `v2.1.0-rc.1`) |
| **GitHub-Release-Typ** | Reguläres GitHub Release (`Latest`) | GitHub Prerelease (`--pre`) |
| **Velopack-Kanal** | `win` | `win-beta` |
| **Windows-Installer** | **`NX-MIX-Setup.exe`** | **`NX-MIX-Beta-Setup.exe`** |
| **Portable-Archiv** | `NX-MIX-Portable.zip` | `NX-MIX-Beta-Portable.zip` |
| **Update-Feed** | `releases.win.json` | `releases.win-beta.json` |
| **UI-Kennzeichnung** | Standard (`NX-MIX`) | Deutliches **`BETA`**-Badge in Sidebar, Fenstertitel & Engine-Tab |

---

## 1. Architekturentscheidung: Kanalumschaltbare Einzelinstallation statt Parallelinstallation

Beide Setup-Varianten (`NX-MIX-Setup.exe` und `NX-MIX-Beta-Setup.exe`) nutzen bewusst dieselbe Velopack-Anwendungskennung (`--packId NxMix`, Installationspfad `%LOCALAPPDATA%\NxMix`) und dieselbe Benutzerkonfiguration (`%APPDATA%\NX-MIX`).

**Technische Begründung:**
1. **Exklusiver Echtzeit-Audiozugriff:** NX-MIX betreibt eine eigenständige Hintergrund-Audio-Engine (`NxMix.Daemon.exe`), die optional per Windows-Logon automatisch im Tray startet. Zwei parallel installierte Daemons würden unkontrolliert um Single-Client-ASIO-Treiber, WASAPI-Exclusive-Endpunkte, globale Windows-App-Umleitungen (`ProcessLoopback`) sowie den lokalen Companion-WebSocket-Port (`127.0.0.1:19480`) konkurrieren.
2. **Nahtloser Kanalwechsel ohne Datenverlust:** Durch die gemeinsame Paket-ID kann jeder Benutzer direkt in NX-MIX unter **Engine -> Updates** zwischen **Stable** und **Beta** wechseln, ohne seine Matrix-Routings, REW-Raumkorrekturen, Fader-Decks oder Snapshots neu einrichten zu müssen.
3. **Zwei klar getrennte Downloads auf GitHub:** Benutzer können bei der Erstinstallation gezielt zwischen `NX-MIX-Setup.exe` (Stable) und `NX-MIX-Beta-Setup.exe` (Beta) wählen; NX-MIX erkennt den installierten Kanal beim ersten Start automatisch.

---

## 2. Veröffentlichungsprozess auf GitHub (Maintainer-Leitfaden)

Die Veröffentlichung wird vollständig reproduzierbar über `.github/workflows/release.yml` und `tools/pack.ps1` gesteuert. Fehlgeschlagene Builds, Unit-Tests oder Headless-UI-Selbsttests (`--selftest`) brechen die Pipeline **vor** jeder Paket- oder Release-Erstellung ab.

### A. Offizielles Stable-Release veröffentlichen (z. B. `v2.0.0`)

1. Optional Release Notes unter `docs/release-notes/2.0.0.md` anlegen.
2. Git-Tag im reinen Format `vX.Y.Z` erstellen und pushen:
   ```powershell
   git tag v2.0.0
   git push origin v2.0.0
   ```
3. GitHub Actions führt automatisch aus:
   - Validierung auf reines Stable-Format (`^\d+\.\d+\.\d+$`) – Beta-Tags können **niemals** versehentlich als Stable veröffentlicht werden.
   - `dotnet build` -> `dotnet test` -> `NX-MIX --selftest`
   - Erzeugung von **`NX-MIX-Setup.exe`**, `NxMix-2.0.0-win-full.nupkg` (+ Delta-Paket) und `releases.win.json`
   - Veröffentlichung als **reguläres GitHub Release**

### B. Beta-Vorabversion veröffentlichen (z. B. `v2.1.0-beta.1`)

1. Optional Release Notes unter `docs/release-notes/2.1.0-beta.1.md` anlegen.
2. Git-Tag mit Prerelease-Suffix (`-beta.N`, `-rc.N` oder `-alpha.N`) erstellen und pushen:
   ```powershell
   git tag v2.1.0-beta.1
   git push origin v2.1.0-beta.1
   ```
3. GitHub Actions führt automatisch aus:
   - Zuordnung zum Kanal `win-beta` (`IS_PRERELEASE=true`)
   - `dotnet build` -> `dotnet test` -> `NX-MIX --selftest`
   - Erzeugung von **`NX-MIX-Beta-Setup.exe`**, `NxMix-2.1.0-beta.1-win-beta-full.nupkg` (+ Delta-Paket) und `releases.win-beta.json`
   - Veröffentlichung ausschließlich als **GitHub Prerelease** (`--pre`) – Stable-Installationen ignorieren dieses Release vollständig.

### C. Trockenlauf ohne Veröffentlichung testen

- **Auf GitHub:** Unter *Actions -> Release -> Run workflow* eine Versionsnummer eingeben (`workflow_dispatch`). Alle Tests und Installer werden gebaut und als Workflow-Artefakt bereitgestellt, aber **nicht** unter GitHub Releases veröffentlicht.
- **Lokal auf Windows:**
  ```powershell
  powershell -ExecutionPolicy Bypass -File tools/pack.ps1 -Version 2.0.0 -Channel stable
  powershell -ExecutionPolicy Bypass -File tools/pack.ps1 -Version 2.1.0-beta.1 -Channel beta
  ```

---

## 3. Update-Kanal in NX-MIX wechseln (Benutzer-Ablauf)

1. In NX-MIX links unten auf **Engine** (`Strg+8`) wechseln.
2. In der linken Karte den Reiter **Updates** öffnen (**Updates & Release Channel**).
3. Unter **Release-Kanal** zwischen **Stable** und **Beta** wählen:
   - Die Auswahl wird sofort persistent in `%APPDATA%\NX-MIX\ui.json` gespeichert.
   - NX-MIX sucht anschließend im gewählten Kanal nach der passenden Zielversion.
4. Über **Updates suchen**, **Changelog** und **Herunterladen / Installieren** lässt sich der gesamte Vorgang transparent steuern.

### Schutz des laufenden Live-Audiobetriebs
- NX-MIX installiert Updates **niemals automatisch im Hintergrund**, während die Audio-Engine (`NxMix.Daemon.exe`) aktiv Audio verarbeitet.
- Ist ein Paket heruntergeladen und per **SHA256-Prüfsumme** verifiziert, wartet NX-MIX auf ausdrückliche Bestätigung:
  - **„Beim Beenden installieren“** *(Standardempfehlung bei laufendem Ton)*: Live-Audio läuft ununterbrochen weiter; erst beim Schließen wird der Daemon kontrolliert beendet und aktualisiert.
  - **„Jetzt kontrolliert neu starten“**: Speichert alle Einstellungen, legt ein Konfigurations-Backup an, fährt `NxMix.Daemon.exe` sauber herunter (inkl. Rücksetzung aktiver Windows-App-Umleitungen) und startet die neue Version.

---

## 4. Konfigurationsschutz, Downgrades (Beta -> Stable) & Wiederherstellung

Beim Wechsel von einer neueren **Beta**-Version (z. B. `2.1.0-beta.2`) zurück auf die aktuelle **Stable**-Version (z. B. `2.0.0`) greifen drei Schutzmechanismen gegen Datenverlust:

1. **Automatisches Voll-Backup vor jedem Update/Downgrade:**
   Vor jeder Update- oder Downgrade-Anwendung sichert NX-MIX sämtliche Konfigurationsdateien in einem zeitgestempelten Ordner unter:
   `%APPDATA%\NX-MIX\backups\<Zeitstempel>_downgrade_<Von>_to_<Nach>\`
   Gesichert werden:
   - `system-config.json` (Hardware-Einbindungen, Takt, App-Links, Companion-API)
   - `live-state.json` (Kanäle, Matrix, Busse, Geräte-Master, REW-Ausgangs-DSP)
   - `ui.json` & `ui-decks.json` (Oberflächeneinstellungen, Kanalwahl & Fader-Decks)
   - `snapshots\*.json` (alle gespeicherten Mischpult-Szenen)
2. **Vorwärtskompatibles JSON-Schema (`[JsonExtensionData]`):**
   Unbekannte Felder aus einer neueren Beta-Version bleiben beim Laden und erneuten Speichern unter einer älteren Stable-Version verlustfrei in den JSON-Dateien erhalten.
3. **Schutz vor beschädigten Dateien (`.corrupt-<Timestamp>.bak`):**
   Sollte eine JSON-Konfigurationsdatei jemals z. B. durch Stromausfall beschädigt sein, überschreibt NX-MIX sie nicht stillschweigend, sondern sichert das Original als `.corrupt-<Zeitstempel>.bak`.

### Manuelle Wiederherstellung im Notfall

Falls nach einem Test im Beta-Kanal ein früherer Stand wiederhergestellt werden soll:
1. In NX-MIX unter **Engine -> Dienst & Takt** auf **„Hintergrund-Engine vollständig beenden …“** klicken.
2. Im Windows-Explorer `%APPDATA%\NX-MIX\backups` öffnen und den gewünschten Sicherungsordner auswählen.
3. Die darin enthaltenen Dateien (`system-config.json`, `live-state.json`, `ui.json`, `ui-decks.json` sowie den Unterordner `snapshots\`) zurück nach `%APPDATA%\NX-MIX\` kopieren.
4. Falls auch das Programmbinärverzeichnis zurückgesetzt werden soll, einfach den gewünschten offiziellen Installer (`NX-MIX-Setup.exe` für Stable) von der GitHub-Releases-Seite ausführen – Benutzerdaten unter `%APPDATA%\NX-MIX` bleiben dabei unangetastet.
