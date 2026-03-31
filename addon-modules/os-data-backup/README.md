# os-data-backup Version 1.0.0 Alpha

Tabellenweises MySQL Backup/Restore/Sync Addon fuer Robust.

Das Addon stellt Robust-Konsolenkommandos bereit, mit denen einzelne Tabellen oder alle Tabellen exportiert, wieder importiert, zwischen Datenbanken kopiert, verglichen, geprueft und repariert werden koennen.

Format pro Tabelle:

- Dateiendung: (OpenSim Table Backup) `.otb`
- Inhalt: `tar.gz` (gzip-komprimiertes tar-Archiv)
- Nutzlast: eine SQL-Datei (Schema + Daten) fuer genau eine Tabelle
- Bei grossen Exporten kann eine Tabelle auf mehrere Teile gesplittet werden: `.part0001.otb`, `.part0002.otb`, ...

## Funktionen

- Hilfe anzeigen (`help`)
- Tabellen auflisten (`list`)
- Einzelne Tabelle exportieren (`export <table>` oder `export <table> <datei.otb|ordner|url>`)
- Alle Tabellen exportieren (`export all <ordner|url>`)
- Tabellen importieren (`import <modus> <table|all> <datei.otb|ordner|url>`)
- Tabellen direkt in eine zweite MySQL-Datenbank kopieren (`copy <modus> <table|all>`)
- Quell- und Ziel-Datenbank vergleichen (`compare <table|all>`)
- Quell- und/oder Zieltabellen mit `CHECK TABLE` pruefen (`check <source|target|both> <table|all>`)
- Quell- und Zieltabellen reparieren/synchronisieren (`repair <modus> <table|all>`)
- Lokales Dateisystem und WebDAV-basierte externe Server/Cloud-Speicher als Backup-Ziel/-Quelle

## Einbindung in Robust

1. In der `ServiceList` den Connector aktivieren:

```ini
SqlDataBackupConnector = "${Const|PrivatePort}/OpenSim.Addons.SqlDataBackup.dll:SqlDataBackup"
```

1. Konfigurationsabschnitt aktivieren:

```ini
[SqlDataBackup]
Enabled = true
; Optional: eigener DB-String, sonst wird [DatabaseService] verwendet
;ConnectionString = ""
; Optional: Ziel-DB fuer copy/compare/check/repair
;TargetConnectionString = ""
CommandPrefix = "sqlbackup"
; Lokaler Ordner oder WebDAV-URL
BackupFolder = "backupOTB"
; Optional fuer WebDAV-Zugriff
;RemoteUser = ""
;RemotePassword = ""
RemoteTimeoutSeconds = 120
; replace | skip | error | merge-replace | merge-skip
DefaultConflictMode = "replace"
; Harte Obergrenze fuer die geschaetzte Groesse einer einzelnen Tabelle
MaxSingleTableExportBytes = 1073741824
; Maximale Groesse eines einzelnen OTB-Teils
MaxOtbPartBytes = 2147483648
```

## Konsolenbefehle

Mit Standard-Praefix:

```text
sqlbackup help
sqlbackup list
sqlbackup export <table>
sqlbackup export <table> <datei.otb|ordner|url>
sqlbackup export all <ordner|url>
sqlbackup import replace <table|all> <datei.otb|ordner|url>
sqlbackup import skip <table|all> <datei.otb|ordner|url>
sqlbackup import error <table|all> <datei.otb|ordner|url>
sqlbackup import merge-replace <table|all> <datei.otb|ordner|url>
sqlbackup import merge-skip <table|all> <datei.otb|ordner|url>
sqlbackup copy replace <table|all>
sqlbackup copy skip <table|all>
sqlbackup copy error <table|all>
sqlbackup copy merge-replace <table|all>
sqlbackup copy merge-skip <table|all>
sqlbackup compare <table|all>
sqlbackup check source <table|all>
sqlbackup check target <table|all>
sqlbackup check both <table|all>
sqlbackup repair replace <table|all>
sqlbackup repair skip <table|all>
sqlbackup repair error <table|all>
sqlbackup repair merge-replace <table|all>
sqlbackup repair merge-skip <table|all>
```

Hinweis: Die Kommandos sind im Addon einzeln registriert (z. B. `sqlbackup import replace`, `sqlbackup copy skip`, `sqlbackup check both`) und nicht als eine einzige ueberladene Sammelfunktion umgesetzt.

## Konfliktmodi

- `replace`: Zielobjekte werden ersetzt. Beim Import aus OTB bedeutet das das bisherige Verhalten mit `DROP TABLE IF EXISTS` und anschliessendem Neuaufbau.
- `skip`: Wenn die Tabelle im Ziel bereits existiert, wird sie komplett uebersprungen.
- `error`: Wenn die Tabelle im Ziel bereits existiert, wird mit Fehler abgebrochen.
- `merge-replace`: Vorhandene Tabelle bleibt bestehen, Zeilen werden mit `REPLACE INTO` geschrieben.
- `merge-skip`: Vorhandene Tabelle bleibt bestehen, Zeilen werden mit `INSERT IGNORE INTO` geschrieben.

## Externer Speicher

- `BackupFolder` kann ein normaler Ordner oder eine WebDAV-URL sein.
- Einzelne Export-/Importkommandos koennen ebenfalls direkt mit einer Datei-URL oder einem WebDAV-Ordner arbeiten.
- Fuer WebDAV werden `RemoteUser` und `RemotePassword` verwendet.
- Typische Beispiele sind Nextcloud, ownCloud, WebDAV-faehige NAS-Systeme oder ein externer HTTPS/WebDAV-Backup-Server.

### Unterstuetzte Ziele und Quellen (alle Varianten)

- Lokaler relativer Ordner

  Beispiel: `backupOTB`

  Verwendbar fuer `BackupFolder`, `export all`, `import all`.

- Lokaler absoluter Ordner (Windows)

  Beispiel: `D:/opensim-backups` oder `D:\\opensim-backups`

  Verwendbar fuer `BackupFolder`, `export all`, `import all`.

- Netzwerkfreigabe (UNC)

  Beispiel: `\\nas01\\opensim\\backups`

  Verwendbar fuer `BackupFolder`, `export all`, `import all`.

- Einzelne lokale OTB-Datei

  Beispiel: `D:/opensim-backups/users.otb`

  Verwendbar fuer `export <table> <datei...>` und `import <modus> <table> <datei...>`.

- HTTP/HTTPS WebDAV-Ordner

  Beispiel: `https://cloud.example.org/remote.php/dav/files/admin/opensim-backups/`

  Verwendbar fuer `BackupFolder`, `export all`, `import all`.

  Wichtig: Das ist ein WebDAV-Endpunkt. `remote.php` ist hier der API-Einstieg, keine Datei fuer SQL-Inhalte.

- HTTP/HTTPS WebDAV-Datei

  Beispiel: `https://cloud.example.org/remote.php/dav/files/admin/opensim-backups/users.otb`

  Verwendbar fuer `export <table> <url>` und `import <modus> <table> <url>`.

- Export einer Tabelle in einen Ordnerpfad

  Wenn bei `sqlbackup export <table> <ziel>` das Ziel als Ordner erkannt wird, wird automatisch ein Dateiname erzeugt: `<tabellenname>_yyyyMMdd_HHmmss.otb`.

  Gilt fuer lokale Ordner und WebDAV-Ordner.

- Dateiendungsergaenzung

  Wenn bei Einzel-Export/Import die Endung fehlt, wird `.otb` automatisch angehaengt.

- Erkennung "all" vs. Einzeldatei

  `export all` und `import ... all` erwarten Ordner/URL-Ordner.

  `export <table> ...` und `import ... <table> ...` erwarten eine Datei oder einen Zielordner (bei Export).

- Authentifizierung bei Remote-Zielen

  Fuer WebDAV nutzt das Addon Basic Auth mit `RemoteUser` und `RemotePassword`.

  Der Server muss `PUT`, `GET`, `DELETE`, `PROPFIND` unterstuetzen.

## Verhalten

- `sqlbackup export <table>` ohne Dateiname speichert automatisch nach `backupOTB/` oder in das konfigurierte `BackupFolder`.
- Der Dateiname wird dann automatisch erzeugt: `<tabellenname>_yyyyMMdd_HHmmss.otb`.
- Bei `export all` wird pro Tabelle mindestens eine Datei erzeugt.
- Wenn eine Tabelle fuer einen einzelnen OTB-Teil zu gross wird, wird sie in mehrere Teile gesplittet.
- Split-Dateien sehen z.B. so aus: `users_20260311_101530.part0001.otb`, `users_20260311_101530.part0002.otb`.
- `import all` verarbeitet auch diese Split-Dateien und ordnet sie ueber den Dateinamen wieder der richtigen Tabelle zu.
- Wenn bei Einzel-Export/Import die Endung fehlt, wird `.otb` automatisch ergaenzt.
- Tabellen werden nicht parallel verarbeitet, sondern strikt hintereinander.
- `export all`, `import all`, `copy all`, `compare all`, `check all` und `repair all` laufen als Hintergrundjob, damit die Konsole nicht blockiert.
- `copy`, `compare`, `check` und `repair` verwenden `TargetConnectionString` als Zielsystem.
- `repair` fuehrt erst `REPAIR TABLE` auf Quelle und Ziel aus und synchronisiert anschliessend abweichende Tabellen in Richtung Ziel.

## Beispiele

```text
sqlbackup list
sqlbackup export users
sqlbackup export users backups/users.otb
sqlbackup export users D:/opensim-backups/
sqlbackup export users https://cloud.example.org/remote.php/dav/files/admin/opensim-backups/users.otb
sqlbackup export all https://cloud.example.org/remote.php/dav/files/admin/opensim-backups/
sqlbackup export all backups
sqlbackup import replace users backups/users.otb
sqlbackup import replace users https://cloud.example.org/remote.php/dav/files/admin/opensim-backups/users.otb
sqlbackup import merge-skip all https://cloud.example.org/remote.php/dav/files/admin/opensim-backups/
sqlbackup copy replace users
sqlbackup compare all
sqlbackup check both all
sqlbackup repair replace users
sqlbackup import replace all backups
```

## Sicherheit und Verhalten

- Tabellennamen werden validiert (`[A-Za-z0-9_]+`).
- Export schreibt SQL mit `DROP TABLE IF EXISTS`, danach `CREATE TABLE` und `INSERT` Zeilen.
- Import fuehrt das SQL aus dem `.otb` Archiv direkt gegen die konfigurierte MySQL-Datenbank aus.
- Bei `merge-replace` und `merge-skip` wird das SQL fuer bestehende Tabellen angepasst statt die Tabelle zu droppen.
- WebDAV-Zugriffe erfolgen ueber HTTP/HTTPS. Der Zielserver muss `PUT`, `GET`, `DELETE` und `PROPFIND` unterstuetzen.
- Bei `all` werden Fehler pro Tabelle/Datei gemeldet, der Rest laeuft weiter.

## Einschraenkungen

- Das Addon ist auf MySQL ausgelegt (`MySql.Data`).
- Das aktuelle OTB-Format arbeitet weiterhin mit SQL-Text im Speicher und ist deshalb nicht fuer echte Multi-TB-Tabellen geeignet.
- `REPAIR TABLE` ist engine-abhaengig. Bei InnoDB kann MySQL hier nur eingeschraenkt oder gar nicht reparieren.
- Der neue `copy`-Pfad arbeitet ebenfalls ueber SQL-Text im Speicher und ist damit fuer extrem grosse Tabellen nicht optimal.
- `MaxSingleTableExportBytes` ist eine Schutzgrenze gegen instabile Exporte sehr grosser Tabellen.
- `MaxOtbPartBytes` teilt die Ausgabe in mehrere Dateien, loest aber nicht das Grundproblem extrem grosser Tabellen im RAM.
- Fuer sehr grosse Tabellen sind externe Streaming-/Snapshot-Backups weiterhin die bessere Wahl.

## Beispielkonfigurationen

Lokales Backup plus zweite Ziel-Datenbank:

```ini
[SqlDataBackup]
Enabled = true
ConnectionString = "Data Source=127.0.0.1;Database=robust;User ID=opensim;Password=secret;Old Guids=true;SslMode=None;"
TargetConnectionString = "Data Source=10.0.0.20;Database=robust_copy;User ID=opensim;Password=secret;Old Guids=true;SslMode=None;"
BackupFolder = "backupOTB"
DefaultConflictMode = "replace"
```

Backup auf WebDAV/Cloud:

```ini
[SqlDataBackup]
Enabled = true
BackupFolder = "https://cloud.example.org/remote.php/dav/files/admin/opensim-backups/"
RemoteUser = "admin"
RemotePassword = "secret"
RemoteTimeoutSeconds = 300
DefaultConflictMode = "merge-skip"
```
