# NX-MIX External Control API v1 (Bitfocus Companion & Stream Deck+)

Die lokale Steuer- und Feedback-API von **NX-MIX** wird direkt vom permanent laufenden Hintergrunddienst **`NxMix.Daemon`** bereitgestellt. Dadurch funktionieren Hardware-Controller wie **Bitfocus Companion**, **Elgato Stream Deck+** (inkl. Drehregler/Encoder), Touch-Oberflächen und Skripte **vollständig unabhängig davon, ob das NX-MIX-Desktopfenster geöffnet oder geschlossen ist**.

## Endpunkte & Sicherheit

- **Standard-Bindung:** `127.0.0.1:19480` (nur lokales Loopback; kein unautorisierter LAN-Zugriff ohne explizite Freigabe in `Engine -> Companion API`).
- **WebSocket (JSON-RPC 2.0 + Echtzeit-Events):** `ws://127.0.0.1:19480/v1/ws`
- **HTTP-Status-Schnappschuss:** `GET http://127.0.0.1:19480/v1/status`
- **Optionale Token-Authentifizierung:** Wenn in der Systemkonfiguration `companionApiAuthToken` gesetzt ist, muss entweder der Header `Authorization: Bearer <TOKEN>` oder der Query-Parameter `?token=<TOKEN>` übergeben werden.

---

## JSON-RPC 2.0 Methoden (`ws://127.0.0.1:19480/v1/ws`)

### 1. System & Zustand
| Methode | Parameter (`params`) | Beschreibung |
|---|---|---|
| `system.ping` | `{}` | Prüft Erreichbarkeit, liefert `pid` und `hostState`. |
| `state.get` | `{}` | Liefert den kompakten Feedback-Zustand aller Geräte-Master, Kanäle, Busse und Snapshots. |
| `panic.setMute` | `{ "mute": true }` | Schaltet den globalen Notfall-Panic-Mute ein oder aus (10 ms Rampe). |

### 2. Kanäle (Fader, Drehregler/Encoder, Mute, Pan)
Kanäle können wahlweise stabil über `"id"` (`Guid`) oder 0-basiert über `"index"` adressiert werden (`"kind": "input"` oder `"output"`).

| Methode | Parameter (`params`) | Beschreibung |
|---|---|---|
| `channel.setGainDb` | `{ "kind": "input", "index": 0, "gainDb": -6.0 }` | Setzt absoluten Kanalpegel in dB. |
| `channel.adjustGainDb` | `{ "kind": "input", "index": 0, "deltaDb": 1.5 }` | **Atomarer Relativschritt für Stream Deck+ Drehregler** (inkl. automatischer Mitnahme gekoppelter Link-Gruppen). |
| `channel.toggleMute` | `{ "kind": "input", "index": 0 }` | Schaltet Kanal-Mute um und liefert `{ "mute": bool }`. |
| `channel.setMute` | `{ "kind": "output", "index": 0, "mute": false }` | Setzt Kanal-Mute gezielt. |
| `channel.setPan` | `{ "kind": "input", "index": 0, "pan": -0.5 }` | Setzt Panorama (`-1.0` links .. `0.0` Mitte .. `+1.0` rechts). |

### 3. Geräte-Master (unabhängig von Einzelkanal-Abständen)
| Methode | Parameter (`params`) | Beschreibung |
|---|---|---|
| `device.setMasterDb` | `{ "index": 0, "gainDb": -10.0 }` | Setzt den Geräte-Master-Pegel ohne Veränderung der relativen Einzelkanal-Fader. |
| `device.adjustMasterDb` | `{ "index": 0, "deltaDb": -1.0 }` | Atomarer Relativschritt für den Geräte-Master (Encoder). |
| `device.toggleMute` | `{ "index": 0 }` | Schaltet den Geräte-Master stumm/aktiv. |
| `device.toggleDim` | `{ "index": 0 }` | Schaltet die Geräte-Master-Absenkung (`Dim`) um. |

### 4. Mix-Busse & Patching-Matrix
| Methode | Parameter (`params`) | Beschreibung |
|---|---|---|
| `bus.setGainDb` | `{ "index": 0, "gainDb": -3.0 }` | Setzt Bus-Summenpegel in dB. |
| `bus.adjustGainDb` | `{ "index": 0, "deltaDb": 0.5 }` | Relativer Schritt für Bus-Pegel. |
| `bus.toggleMute` | `{ "index": 0 }` | Schaltet Bus-Mute um. |
| `route.setCrosspoint` | `{ "inputIndex": 0, "outputIndex": 1, "enabled": true, "gainDb": 0.0 }` | Setzt oder löscht (`"enabled": false`) einen Matrix-Kreuzpunkt unterbrechungsfrei. |

### 5. Snapshots
| Methode | Parameter (`params`) | Beschreibung |
|---|---|---|
| `snapshot.list` | `{}` | Listet alle gespeicherten Mischpult-Snapshots auf. |
| `snapshot.recall` | `{ "name": "Streaming", "fadeMs": 250 }` | Ruft einen Snapshot per `"id"` oder `"name"` mit Sample-genauem Übergangs-Fade ab (`RecallSafe` wird beachtet; Hardware-Setup bleibt unberührt). |

### 6. Gedrosselte Pegel-Subscriptions (Meter-Feedback)
| Methode | Parameter (`params`) | Beschreibung |
|---|---|---|
| `meters.subscribe` | `{ "fps": 15 }` | Aktiviert Push-Pegelpakete (`1..30 Hz`; `0` zum Abbestellen). Langsame Netzwerk-Clients blockieren dank `DropOldest`-Queue niemals die Audio-Engine. |

---

## Server-Push-Events

1. **`state.updated`**: Wird sofort nach Verbindungsaufbau sowie bei jeder Zustandsänderung (egal ob durch UI, Tray, Snapshot oder anderen Controller ausgelöst) gesendet, damit Companion-Tastenfarben und OLED-Anzeigen auf dem Stream Deck+ verzögerungsfrei synchron bleiben.
2. **`meters.frame`**: Enthält `{ "inputs": [...], "outputs": [...], "buses": [...], "devices": [...] }` als lineare Spitzenwerte (`0.0 .. 1.0+`), sofern `meters.subscribe` aktiv ist.
