# SQL Liste

## SQL-DML (Data Manipulation Language)

- **SELECT** - dient zum Abfragen und Auslesen von Daten aus einer oder mehreren Tabellen
- **SELECT DISTINCT** - liefert nur eindeutige (einmalige) Werte zurück und unterdrückt Duplikate
- **SELECT INTO** - erstellt eine neue Tabelle und fügt die Abfrageergebnisse direkt ein
- **FROM** - gibt die Tabelle(n) an, aus denen die Daten abgefragt werden sollen
- **WHERE** - filtert Datensätze basierend auf einer bestimmten Bedingung
- **GROUP BY** - fasst Datensätze mit gleichen Werten in Gruppen zusammen
- **HAVING** - filtert Gruppen basierend auf einer Bedingung (wie WHERE für Gruppen)
- **ORDER BY** - sortiert das Abfrageergebnis aufsteigend oder absteigend
- **JOIN** - verbindet Datensätze aus zwei oder mehr Tabellen miteinander
- **INNER JOIN** - gibt nur Datensätze zurück, die in beiden verknüpften Tabellen vorhanden sind
- **LEFT JOIN** - gibt alle Datensätze der linken Tabelle und passende der rechten Tabelle zurück
- **RIGHT JOIN** - gibt alle Datensätze der rechten Tabelle und passende der linken Tabelle zurück
- **FULL OUTER JOIN** - gibt alle Datensätze zurück, wenn sie in einer der beiden Tabellen vorhanden sind
- **CROSS JOIN** - bildet das kartesische Produkt aller Datensätze beider Tabellen
- **UNION** - kombiniert die Ergebnismengen mehrerer Abfragen und entfernt Duplikate
- **UNION ALL** - kombiniert die Ergebnismengen mehrerer Abfragen und behält Duplikate bei
- **INTERSECT** - gibt nur die Datensätze zurück, die in allen Abfragen vorkommen
- **EXCEPT** - gibt Datensätze der ersten Abfrage zurück, die nicht in der zweiten vorkommen
- **INSERT INTO** - fügt neue Datensätze in eine Tabelle ein
- **INSERT INTO SELECT** - übernimmt Abfrageergebnisse und fügt sie in eine andere Tabelle ein
- **UPDATE** - ändert vorhandene Datensätze in einer Tabelle
- **DELETE** - entfernt vorhandene Datensätze aus einer Tabelle
- **MERGE** - führt Einfüge-, Änderungs- und Löschoperationen in einem Befehl zusammen
- **TRUNCATE** - entfernt alle Datensätze einer Tabelle, behält aber die Tabellenstruktur bei
- **WITH** - definiert temporäre Abfrageergebnisse für komplexe Abfragen
- **IN** - prüft, ob ein Wert in einer Liste oder Teilabfrage vorkommt
- **BETWEEN** - prüft, ob ein Wert innerhalb eines bestimmten Bereichs liegt
- **LIKE** - sucht nach einem bestimmten Muster in einer Textspalte
- **IS NULL** - prüft, ob ein Wert NULL (leer/unbekannt) ist
- **IS NOT NULL** - prüft, ob ein Wert nicht NULL ist
- **EXISTS** - prüft, ob eine Unterabfrage mindestens einen Datensatz liefert
- **ANY** - vergleicht einen Wert mit mindestens einem Wert einer Unterabfrage
- **ALL** - vergleicht einen Wert mit allen Werten einer Unterabfrage
- **CASE** - ermöglicht Fallunterscheidungen und bedingte Berechnungen
- **COALESCE** - gibt den ersten Nicht-NULL-Wert aus einer Liste zurück
- **NULLIF** - gibt NULL zurück, wenn zwei Ausdrücke gleich sind, sonst den ersten Ausdruck
- **CAST** - wandelt einen Wert in einen anderen Datentyp um
- **CONVERT** - wandelt einen Wert in einen anderen Datentyp um (systemspezifisch)
- **COUNT** - zählt die Anzahl der Datensätze
- **SUM** - berechnet die Summe aller Werte einer Spalte
- **AVG** - berechnet den Durchschnittswert aller Werte einer Spalte
- **MIN** - findet den kleinsten Wert einer Spalte
- **MAX** - findet den größten Wert einer Spalte
- **TOP** - begrenzt die Anzahl der zurückgegebenen Datensätze (SQL Server)
- **LIMIT** - begrenzt die Anzahl der zurückgegebenen Datensätze (MySQL, PostgreSQL)
- **OFFSET** - überspringt eine bestimmte Anzahl von Datensätzen (für Paginierung)
- **FETCH** - begrenzt die Anzahl der zurückgegebenen Datensätze (Oracle, DB2)
- **FOR UPDATE** - sperrt die ausgewählten Datensätze für andere Transaktionen

## SQL-DDL (Data Definition Language)

- **CREATE** - erstellt ein neues Datenbankobjekt wie eine Tabelle, Datenbank oder einen Index
- **ALTER** - ändert die Struktur eines bestehenden Datenbankobjekts
- **DROP** - löscht ein gesamtes Datenbankobjekt inklusive aller seiner Daten
- **TRUNCATE** - entfernt alle Datensätze einer Tabelle, behält aber die Tabellenstruktur bei
- **RENAME** - benennt ein vorhandenes Datenbankobjekt um
- **COMMENT** - fügt Kommentare zu Datenbankobjekten hinzu
- **CREATE DATABASE** - erstellt eine neue Datenbank
- **ALTER DATABASE** - ändert die Eigenschaften einer bestehenden Datenbank
- **DROP DATABASE** - löscht eine gesamte Datenbank inklusive aller Tabellen und Daten
- **CREATE TABLE** - erstellt eine neue Tabelle mit Spalten und Datentypen
- **ALTER TABLE** - fügt Spalten hinzu, ändert oder löscht Spalten in einer Tabelle
- **DROP TABLE** - löscht eine gesamte Tabelle inklusive aller Datensätze
- **CREATE INDEX** - erstellt einen Index für schnelleren Datenzugriff
- **ALTER INDEX** - ändert die Eigenschaften eines bestehenden Index
- **DROP INDEX** - löscht einen vorhandenen Index
- **CREATE VIEW** - erstellt eine virtuelle Tabelle basierend auf einer Abfrage
- **ALTER VIEW** - ändert die Definition einer bestehenden Ansicht
- **DROP VIEW** - löscht eine vorhandene Ansicht
- **CREATE SCHEMA** - erstellt ein Schema zur Strukturierung von Datenbankobjekten
- **ALTER SCHEMA** - ändert die Eigenschaften eines bestehenden Schemas
- **DROP SCHEMA** - löscht ein vorhandenes Schema
- **CREATE USER** - erstellt einen neuen Datenbankbenutzer
- **ALTER USER** - ändert die Eigenschaften eines bestehenden Benutzers
- **DROP USER** - löscht einen vorhandenen Datenbankbenutzer
- **CREATE ROLE** - erstellt eine neue Rolle zur Rechtevergabe
- **ALTER ROLE** - ändert die Eigenschaften einer bestehenden Rolle
- **DROP ROLE** - löscht eine vorhandene Rolle
- **CREATE PROCEDURE** - erstellt eine gespeicherte Prozedur
- **ALTER PROCEDURE** - ändert eine bestehende gespeicherte Prozedur
- **DROP PROCEDURE** - löscht eine vorhandene gespeicherte Prozedur
- **CREATE FUNCTION** - erstellt eine benutzerdefinierte Funktion
- **ALTER FUNCTION** - ändert eine bestehende benutzerdefinierte Funktion
- **DROP FUNCTION** - löscht eine vorhandene benutzerdefinierte Funktion
- **CREATE TRIGGER** - erstellt einen Trigger, der bei bestimmten Ereignissen ausgeführt wird
- **ALTER TRIGGER** - ändert einen bestehenden Trigger
- **DROP TRIGGER** - löscht einen vorhandenen Trigger
- **CREATE SEQUENCE** - erstellt eine Sequenz zur automatischen Nummernvergabe
- **ALTER SEQUENCE** - ändert eine bestehende Sequenz
- **DROP SEQUENCE** - löscht eine vorhandene Sequenz
- **CREATE SYNONYM** - erstellt einen Alias für ein Datenbankobjekt
- **DROP SYNONYM** - löscht einen vorhandenen Alias
- **CREATE TABLESPACE** - erstellt einen Speicherbereich für Datenbankobjekte
- **ALTER TABLESPACE** - ändert einen bestehenden Tablespace
- **DROP TABLESPACE** - löscht einen vorhandenen Tablespace
- **CREATE DOMAIN** - erstellt einen benutzerdefinierten Datentyp
- **ALTER DOMAIN** - ändert einen bestehenden benutzerdefinierten Datentyp
- **DROP DOMAIN** - löscht einen vorhandenen benutzerdefinierten Datentyp
- **CREATE COLLATION** - erstellt eine Sortierreihenfolge für Zeichenketten
- **DROP COLLATION** - löscht eine vorhandene Sortierreihenfolge
- **CREATE EXTENSION** - fügt eine Erweiterung zur Datenbank hinzu
- **DROP EXTENSION** - entfernt eine vorhandene Erweiterung aus der Datenbank

## SQL-DCL (Data Control Language)

- **GRANT** - erteilt bestimmten Benutzern oder Rollen spezifische Zugriffsrechte auf Datenbankobjekte
- **REVOKE** - entzieht zuvor gewährte Berechtigungen von Benutzern oder Rollen
- **DENY** - verweigert explizit eine bestimmte Berechtigung für ein Sicherheitsobjekt
- **WITH GRANT OPTION** - erlaubt dem Berechtigten, die erhaltenen Rechte selbst weiterzugeben

## SQL-TCL (Transaction Control Language)

- **COMMIT** - speichert alle Änderungen der aktuellen Transaktion dauerhaft in der Datenbank
- **ROLLBACK** - macht alle Änderungen der aktuellen Transaktion rückgängig
- **SAVEPOINT** - setzt einen Marker innerhalb einer Transaktion, zu dem später zurückgekehrt werden kann
- **ROLLBACK TO SAVEPOINT** - macht alle Änderungen bis zu einem bestimmten Sicherungspunkt rückgängig
- **RELEASE SAVEPOINT** - löscht einen zuvor definierten Sicherungspunkt
- **SET TRANSACTION** - definiert Eigenschaften für die nächste Transaktion
- **SET CONSTRAINT** - legt fest, wann Constraints innerhalb einer Transaktion geprüft werden
- **BEGIN TRANSACTION** - startet explizit eine neue Transaktion
- **END TRANSACTION** - beendet die aktuelle Transaktion

## SQL-SCL (Session Control Language)

- **ALTER SESSION** - ändert die Einstellungen für die aktuelle Datenbanksitzung
- **SET ROLE** - aktiviert oder deaktiviert Rollen für die aktuelle Sitzung
- **SET NAMES** - legt den Zeichensatz für die aktuelle Verbindung fest
- **SET TIME ZONE** - definiert die Zeitzone für die aktuelle Sitzung

## SQL-SYS (System-/Admin-Befehle, herstellerspezifisch)

- **ALTER SYSTEM** - ändert systemweite Einstellungen der Datenbankinstanz
- **SHUTDOWN** - fährt die Datenbankinstanz herunter
- **STARTUP** - startet die Datenbankinstanz
- **CHECKPOINT** - erzwingt einen Schreibpunkt für alle geänderten Daten
- **FLUSH** - leert bestimmte Puffer oder Caches

## SQL-CPL (Cursor/Prepared/Embedded SQL, herstellerspezifisch)

- **DECLARE CURSOR** - deklariert einen Cursor für eine Abfrage
- **OPEN** - öffnet einen Cursor
- **FETCH** - ruft die nächste Zeile eines Cursors ab
- **CLOSE** - schließt einen geöffneten Cursor
- **PREPARE** - bereitet eine SQL-Anweisung zur Ausführung vor
- **EXECUTE** - führt eine vorbereitete SQL-Anweisung aus
- **EXECUTE IMMEDIATE** - führt eine SQL-Anweisung sofort aus
- **WHENEVER** - definiert Fehlerbehandlungsroutinen für eingebettetes SQL

## SQL-CCL (Connection Control Language)

- **CONNECT** - stellt eine Verbindung zu einer Datenbank her
- **DISCONNECT** - beendet eine bestehende Datenbankverbindung
- **SET CONNECTION** - wechselt zwischen mehreren Verbindungen

## SQL-DIAG (Diagnose-/Hilfsbefehle, herstellerspezifisch)

- **DESCRIBE** - zeigt die Struktur eines Datenbankobjekts an
- **EXPLAIN PLAN** - zeigt den Ausführungsplan einer Abfrage an
- **SHOW** - zeigt Informationen über Datenbankobjekte oder Einstellungen an
- **HELP** - ruft Hilfeinformationen zu SQL-Befehlen auf
