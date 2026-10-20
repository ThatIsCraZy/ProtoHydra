# Brocade configdownload: SFTP-Abbruch und FTP-Datenports

Stand: 20.10.2026. Grundlage ist `transfer-capture-20261020-104709.log` sowie die
Reproduktion gegen die echten ProtoHydra-Listener mit SSH.NET und FluentFTP.

## SFTP: Ursache im Capture belegt und reproduziert

Um 10:47:23 wurde `scp -f sub/ROFC05.txt` erfolgreich ausgeführt; der Server hat
150.546 Bytes übertragen. Beim SFTP-Versuch um 10:48:06 waren Anmeldung und Start
des SFTP-v3-Subsystems ebenfalls erfolgreich. Der erste `REALPATH`-Aufruf führte
anschließend zu zweimal `Path rejected` für `/.`. Ein Datei-Download wurde gar
nicht begonnen.

`/.` bezeichnet das aktuelle Verzeichnis im virtuellen Server-Root. Der gemeinsame
Pfadschutz verwarf bisher sowohl `.` als auch `..` pauschal als Traversal.
Der SFTP-Adapter behandelte zwar den exakten Pfad `.` gesondert, aber nicht `/.`
oder eingebettete `./`-Segmente. Das erklärt auch den Unterschied zu SCP:
Der erfolgreiche SCP-Aufruf adressierte die Datei direkt und benötigte keinen
vorherigen `REALPATH`-Aufruf.

Der Fix entfernt einzelne `.`-Segmente im gemeinsamen Resolver vor dem
Dateisystemzugriff. `..`, Laufwerksangaben, Backslashes, doppelt kodiertes
Traversal und Reparse-Points bleiben gesperrt. Der SFTP-Adapter bewahrt außerdem
führende Slashes für die zentrale UNC-Prüfung, statt `//` vorab zu entfernen.
SFTP-Pfade werden weiterhin nicht als URLs prozentdekodiert.

## FTP: separater Fehler nachgewiesen, Switch-Ursache noch offen

Im gelieferten Capture sind keine FTP-Verbindungen enthalten. Eine genaue
Aussage über den Abbruch am Switch ist deshalb nicht möglich.

Die lokalen Tests zeigten einen eigenständigen Konfigurationsfehler:
`Protocols.FtpPassivePortRange` war mit `50000-50100` vorhanden, wurde aber weder
an den FTP-/FTPS-Listener weitergereicht noch bei Firewall-Prüfung und temporärer
Firewall-Freigabe berücksichtigt. `PASV` und `EPSV` verwendeten dadurch vom
Betriebssystem gewählte Ports; vor dem Fix wurden beispielsweise 55372 und 55376
gemessen. Die Anzeige „Firewall allows all checked ports“ konnte gleichzeitig
grün sein, obwohl nur der Steuerport und keine passiven Datenports geprüft wurden.

Eine erfolgreiche FTP-Anmeldung auf Port 21 reicht für eine Dateiübertragung
nicht aus: Im passiven Modus muss der Switch zusätzlich den vom Server angebotenen
Datenport erreichen. Eine Freigabe allein für Port 21 oder allein für den bisher
wirkungslosen Bereich 50000–50100 konnte deshalb zu Abbrüchen führen. Ob dieser
Fall beim betroffenen Switch vorlag, muss ein erneuter Test zeigen.

Der Fix validiert den konfigurierten Bereich und übergibt ihn über die vorhandenen
`SimplePasvOptions` an FubarDev. Dieselbe Einstellung wird jetzt für die
Firewall-Prüfung und für die vom Benutzer aktivierbare temporäre Freigabe genutzt.
Es wurde keine eigene FTP-Protokolllogik ergänzt und kein Paket ausgetauscht.

## Validierung

`dotnet build ProtoHydra.slnx --no-restore` ist erfolgreich. Die drei vorhandenen
Avalonia-Warnungen zu `TextBox.Watermark` in `AuthenticationWindow.axaml` sind
unabhängig von diesem Fix.

`dotnet test ProtoHydra.slnx --no-restore` besteht mit 165 Core-Tests und 61
Integrationstests, ohne übersprungene Tests. Davon sind 26 neue
Kompatibilitätsfälle in `BrocadeTransferCompatibilityTests`:

- SFTP-Root-Aliase und eingebettete `.`-Segmente, inklusive `REALPATH("/.")`.
- Blockierte Traversal-, UNC-, Laufwerks- und Backslash-Pfade.
- Echte SFTP-Verbindung mit Root-Auflösung, Stat und Download einer synthetischen
  150.546-Byte-Datei unter `sub/ROFC05.txt`, inklusive Transfer-Events.
- FTP-Download derselben synthetischen Datei mit Verzeichniswechsel über `PASV`,
  `EPSV`, `PORT` und `EPRT`.
- Tatsächlicher Daten-Endpunkt innerhalb des Standardbereichs und eines
  benutzerdefinierten Bereichs; zusätzlich FTPS mit `PASV` und `EPSV`.
- Verständliche Ablehnung ungültiger passiver Portbereiche vor Listenerstart.

Zusätzliche Core-Tests sichern die Bereichsvalidierung und die Firewall-Auswertung:
Eine Freigabe nur für Port 21 ergibt bei gesperrten Datenports keinen grünen
Gesamtstatus mehr; eine Freigabe für `21,50000-50100` deckt beide ab.

Die Tests verwenden reguläre Clients gegen lokale Listener. Ein physischer
Brocade-Switch, dessen Fabric-OS-Version und die Netzwerk-Firewalls des betroffenen
Standorts standen für diese Validierung nicht zur Verfügung.

## Test am betroffenen Switch

1. [ProtoHydra v1.7.2](https://github.com/ThatIsCraZy/ProtoHydra/releases/tag/v1.7.2)
   starten und den bisherigen Root-Ordner wählen.
2. Als Remote-Datei weiterhin `sub/ROFC05.txt` relativ zum ProtoHydra-Root angeben;
   keinen lokalen Windows-Dateipfad angeben.
3. SFTP aktivieren und `configdownload` mit demselben Ablauf erneut ausführen.
   `REALPATH` darf nicht mehr mit `Path rejected` für `/.` abbrechen.
4. Für passives FTP müssen der FTP-Steuerport und der konfigurierte passive
   TCP-Portbereich vom Switch zum Server erreichbar sein, standardmäßig
   `21/TCP` und `50000-50100/TCP`. Die aktualisierte Firewall-Anzeige prüft nun
   beides. Bereits aktive temporäre Freigaben einer alten EXE müssen mit dem
   neuen Build neu erstellt werden. Zwischenliegende Netzwerk-Firewalls werden
   von der Windows-Firewall-Anzeige nicht geprüft.
5. Im aktiven FTP-Modus (`PORT`/`EPRT`) baut der Server die Datenverbindung zum
   vom Switch angegebenen Port auf. Diese Gegenrichtung muss im Netz erlaubt
   sein; der passive Server-Portbereich betrifft diesen Modus nicht.
6. Falls FTP weiterhin abbricht, vor dem Versuch den Transfer-Capture starten und
   auch die genaue Fehlermeldung des Switches sowie dessen FOS-Version erfassen.
   Erst damit lässt sich die verbleibende Ursache dem Login, Dateizugriff oder
   Datenkanal zuordnen.

## Primärquellen

- [FubarDev 3.1.2: SimplePasvOptions](https://github.com/FubarDevelopment/FtpServer/blob/v3.1.2/src/FubarDev.FtpServer/SimplePasvOptions.cs)
  beschreibt die bereits vorhandenen Einstellungen für den passiven Portbereich.
- [Brocade Fabric OS 8.2.x Command Reference](https://docs.broadcom.com/doc/FOS-82x-Command-RM)
  beschreibt `configDownload` und die Protokollauswahl. Das ist eine Referenz,
  keine Aussage über die unbekannte FOS-Version des betroffenen Switches.

Die bisherige Testlücke war, dass erfolgreiche Dateidownloads weder die konkrete
Root-Schreibweise `/.` noch die tatsächlich benutzten passiven Datenports
absicherten. Die neuen Regressionstests prüfen beide Grenzen ausdrücklich.
