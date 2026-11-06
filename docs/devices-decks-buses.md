# Geräte, Decks und Busse — lokaler Entwicklungsstand

Die Änderungen sind nicht veröffentlicht. Ein Release erfolgt ausschließlich auf ausdrücklichen Auftrag.

## Bedienung

- Geräte → Hinzufügen: Windows, ASIO oder Software wählen. Geräte werden mit ihren verfügbaren Kanälen (bis 64 je Zuordnung) hinzugefügt. Die Zuordnungstabelle erlaubt anschließend die genaue Kanalbelegung.
- Ein hinzugefügtes Gerät anklicken: Geräteeinstellungen und Entfernen. ASIO-Abtastraten und Puffergrößen stammen aus den Fähigkeiten des Treibers. Die Einstellungen gelten gemeinsam für Ein- und Ausgänge desselben ASIO-Treibers. Windows-Geräte öffnen die Windows-Soundeinstellungen.
- Mixer → + Deck: eigene Ansicht anlegen. Im Layout-Editor einen Platz anklicken und einen Eingang, Ausgang oder Bus auswählen. Plätze per Ziehen tauschen. Leerstellen bleiben erhalten. Unter „Deck …“ lassen sich Name, Spalten und Reihen ändern sowie Decks kopieren oder löschen.
- Decks enthalten Ansichten derselben Kanäle, keine unabhängigen Audio-Mischungen. Das Entfernen eines Faders aus einem Deck verändert keine Audioverbindungen.
- Mixer → + Bus: 1–64 Kanäle wählen. Der Bus-Fader steuert alle Kanäle gemeinsam. „Routing“ öffnet die Zuweisungen zwischen Quellen, Bus-Kanälen, weiteren Bussen und Ausgängen. Busse haben keinen eigenen EQ, Delay oder Limiter.
- Die erste Zuweisung eines Eingangs zu einem Bus entfernt dessen direkte Ausgangsverbindungen. Mehrere Bus-Ziele sind möglich. Bus-Kaskaden sind erlaubt, Rückkopplungsschleifen nicht.
- Die bestehende Ausgangsbearbeitung einschließlich REW-Korrektur bleibt am Ausgang. Sie wird nach der Mischung angewendet.

## Technischer Stand

Projektformat 3 speichert stabile Kanal-IDs, Busse, ihre Verbindungen, Geräteeinstellungen und Decks. Ältere Projekte bleiben lesbar. Für die Audioberechnung wird das lineare Bus-Netz außerhalb des Audiothreads in Mischkoeffizienten übersetzt; Änderungen werden durch die bestehende Gain-Rampe geglättet. Es gibt keine Filter oder Limiter innerhalb des Bus-Netzes.

Native ASIO-Ein- und Ausgänge verwenden NAudio.Asio 3.3.0. Ein Treiber wird für alle seine Zuordnungen gemeinsam geöffnet. Mehrere Treiber sowie ASIO und WASAPI können über getrennte Ringpuffer und adaptives Resampling an derselben Mixer-Engine teilnehmen. Wenn kein geeigneter WASAPI-Ausgang den Takt liefert, wird der vorhandene Software-Takt verwendet. Das ist noch kein auf minimale ASIO-Latenz optimierter Treibertakt.

Die Integration wurde mit dem lokal installierten FlexASIO und ausschließlich Stille geprüft: ASIO allein mit 48 kHz sowie gemeinsam mit einem Windows-Ausgang bei 44,1 kHz ASIO-Abtastrate und 48 kHz Engine-Abtastrate. Beide Start-/Stopp-Tests bestanden. Eine fehlerhafte lokale FlexASIO-Konfiguration wurde mit Zustimmung ausschließlich für den Test geändert und danach exakt wiederhergestellt. Das bestätigt keinen Betrieb am UMC1820 und keinen Betrieb mit mehreren physischen ASIO-Interfaces; dafür fehlen hier die Geräte.

Die Release-Konfiguration baut ohne Warnungen oder Fehler. Die Testsuite ohne Performance-Kategorie meldete 245 bestandene Fälle; Hardware-Tests ohne gesetztes `NXMIX_TEST_ASIO` führen keinen ASIO-Gerätetest aus. Die beiden oben genannten ASIO-Fälle wurden zusätzlich mit gesetzter Variable ausgeführt. Der App-Selbsttest bestand mit 212 Prüfungen. Die Deck- und Geräteansichten wurden außerdem bei unterschiedlichen Fenstergrößen und UI-Skalierungen als Screenshots geprüft.

## Noch offen

- Eigene virtuelle NX-MIX-Ein-/Ausgänge im Windows-Gerätemanager: Dafür fehlt ein eigener Windows-Audiotreiber samt WDK-Build, Kommunikation mit der App und signierter Installation. Programme und bereits installierte virtuelle Audiogeräte sind verwendbar. Es wird kein virtueller NX-MIX-Treiber vorgetäuscht oder ungefragt installiert.
- Hardware-Abnahme mit dem UMC1820: alle Ein-/Ausgangskanäle, unterschiedliche Gerätesampleraten, Langzeit-Taktdrift, Abziehen/Wiederanstecken und Treiber-Reset.
- Bus-Pegelmeter: Die Bus-Fader regeln Audio, besitzen derzeit aber keine eigene Pegelanzeige.

## Quellen

- [NAudio ASIO Duplex](https://github.com/naudio/NAudio/blob/main/Docs/AsioDuplex.md)
- [Microsoft SysVAD](https://learn.microsoft.com/en-us/samples/microsoft/windows-driver-samples/sysvad-virtual-audio-device-driver-sample/)
- [Windows-Treibersignierung](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/driver-signing)
