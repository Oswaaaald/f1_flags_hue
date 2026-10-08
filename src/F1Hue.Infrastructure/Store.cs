using System.Text.Json;
using Microsoft.Data.Sqlite;
using F1Hue.Core;

namespace F1Hue.Infrastructure;

public sealed record RecordedSession(string Key, int Flags, string At, string Name);

public sealed class Store : ISettingsStore, IDisposable
{
    public const int DatabaseVersion = 2;
    private readonly SqliteConnection _db;
    private readonly ProfileLease _lease;
    private readonly object _gate = new();
    private AppSettings _settings = new();
    private RaceEvent[]? _recent;
    private RecordedSession[]? _sessions;
    public string DirectoryPath
    {
        get;
    }
    public event Action? Changed;

    public Store(string directory)
    {
        DirectoryPath = Path.GetFullPath(directory);
        PrivateFiles.Directory(DirectoryPath);
        _lease = new ProfileLease(DirectoryPath);
        var path = Path.Combine(DirectoryPath, "f1-hue.sqlite3");
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        try
        {
            _db.Open();
            using var version = _db.CreateCommand();
            version.CommandText = "PRAGMA user_version";
            var current = Convert.ToInt32(version.ExecuteScalar());
            if (current > DatabaseVersion)
                throw new InvalidOperationException($"Cette base utilise le format {current}, plus récent que cette application ({DatabaseVersion}). Mets l’application à jour ou restaure une sauvegarde compatible.");
            if (current > 0)
                (Get<AppSettings>("settings") ?? new()).Validate();
            Execute("PRAGMA busy_timeout=5000;");
            if (current is > 0 and < DatabaseVersion)
                BackupCore("avant-migration-" + current);
            using (var transaction = _db.BeginTransaction())
            {
                using var migration = _db.CreateCommand();
                migration.Transaction = transaction;
                migration.CommandText = """
                    CREATE TABLE IF NOT EXISTS kv (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                    CREATE TABLE IF NOT EXISTS events (id INTEGER PRIMARY KEY, session_key TEXT, received_at TEXT NOT NULL, body TEXT NOT NULL);
                    CREATE INDEX IF NOT EXISTS events_session ON events(session_key, id);
                    """;
                migration.ExecuteNonQuery();
                if (current < 2)
                {
                    migration.CommandText = """
                        ALTER TABLE events ADD COLUMN kind TEXT;
                        UPDATE events SET kind=json_extract(body,'$.kind');
                        CREATE INDEX events_flag_session ON events(session_key,id) WHERE kind='flag';
                        CREATE TABLE event_sessions(session_key TEXT PRIMARY KEY, flags INTEGER NOT NULL, latest INTEGER NOT NULL, name TEXT NOT NULL, at TEXT NOT NULL);
                        CREATE INDEX event_sessions_latest ON event_sessions(latest DESC);
                        INSERT INTO event_sessions SELECT e.session_key,s.flags,s.latest,COALESCE(json_extract(e.body,'$.sessionName'),'Séance F1'),e.received_at
                        FROM (SELECT session_key,COUNT(*) flags,MAX(id) latest FROM events WHERE kind='flag' AND session_key IS NOT NULL GROUP BY session_key) s JOIN events e ON e.id=s.latest;
                        CREATE TRIGGER event_session_insert AFTER INSERT ON events WHEN NEW.kind='flag' AND NEW.session_key IS NOT NULL BEGIN
                            INSERT INTO event_sessions VALUES(NEW.session_key,1,NEW.id,COALESCE(json_extract(NEW.body,'$.sessionName'),'Séance F1'),NEW.received_at)
                            ON CONFLICT(session_key) DO UPDATE SET flags=flags+1,latest=excluded.latest,name=excluded.name,at=excluded.at;
                        END;
                        CREATE TRIGGER event_session_delete AFTER DELETE ON events WHEN OLD.kind='flag' AND OLD.session_key IS NOT NULL BEGIN
                            UPDATE event_sessions SET flags=flags-1 WHERE session_key=OLD.session_key;
                            DELETE FROM event_sessions WHERE flags<=0;
                        END;
                        PRAGMA user_version=2;
                        """;
                    migration.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            Execute("PRAGMA journal_mode=WAL;");
            PrivateFiles.Protect(path);
            _settings = (Get<AppSettings>("settings") ?? new()).Validate();
        }
        catch { _db.Dispose(); _lease.Dispose(); throw; }
    }

    private void Execute(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
    public T? Get<T>(string key)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT value FROM kv WHERE key=$key";
            cmd.Parameters.AddWithValue("$key", key);
            return cmd.ExecuteScalar() is string text ? JsonSerializer.Deserialize<T>(text, JsonDefaults.Options) : default;
        }
    }
    public void Put<T>(string key, T value)
    {
        lock (_gate)
        {
            if (key == "settings")
                JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(value, JsonDefaults.Options), JsonDefaults.Options)!.Validate();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO kv(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", JsonSerializer.Serialize(value, JsonDefaults.Options));
            cmd.ExecuteNonQuery();
            if (key == "settings")
                _settings = (Get<AppSettings>(key) ?? new()).Validate();
        }
    }
    public void Delete(string key)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM kv WHERE key=$key";
            cmd.Parameters.AddWithValue("$key", key);
            cmd.ExecuteNonQuery();
            if (key == "settings")
                _settings = new();
        }
    }
    private static AppSettings Copy(AppSettings value) => value with { LightIds = value.LightIds.ToArray(), Effects = new(value.Effects) };
    public AppSettings Read()
    {
        lock (_gate)
            return Copy(_settings);
    }
    public void Save(AppSettings settings) => Save(settings, null);
    public void Save(AppSettings settings, long? expectedRevision)
    {
        lock (_gate)
        {
            if (expectedRevision is long expected && expected != _settings.Revision)
                throw new SettingsConflictException();
            Put("settings", Copy(settings.Validate()) with
            {
                Revision = checked(_settings.Revision + 1)
            });
        }
        Changed?.Invoke();
    }
    public bool Append(RaceEvent item)
    {
        if (item.Kind is not ("flag" or "lap" or "session_status") || item.Initial)
            return false;
        lock (_gate)
        {
            using var transaction = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "INSERT INTO events(session_key,received_at,body,kind) VALUES($session,$at,$body,$kind); DELETE FROM events WHERE id <= (SELECT COALESCE(MAX(id),0)-100000 FROM events);";
            cmd.Parameters.AddWithValue("$session", (object?)item.SessionKey ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$at", item.ReceivedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$body", JsonSerializer.Serialize(item, JsonDefaults.Options));
            cmd.Parameters.AddWithValue("$kind", item.Kind);
            cmd.ExecuteNonQuery();
            transaction.Commit();
            _recent = null;
            _sessions = null;
        }
        Changed?.Invoke();
        return true;
    }
    public RaceEvent[] Events(string? sessionKey = null, int limit = 100)
    {
        lock (_gate)
        {
            if (sessionKey is null && limit == 60 && _recent is not null)
                return _recent.ToArray();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sessionKey is null ? "SELECT body FROM events ORDER BY id DESC LIMIT $limit" : "SELECT body FROM events WHERE session_key=$session ORDER BY id DESC LIMIT $limit";
            if (sessionKey is not null)
                cmd.Parameters.AddWithValue("$session", sessionKey);
            cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100000));
            using var reader = cmd.ExecuteReader();
            var items = new List<RaceEvent>();
            while (reader.Read())
                items.Add(JsonSerializer.Deserialize<RaceEvent>(reader.GetString(0), JsonDefaults.Options)!);
            var result = items.ToArray();
            if (sessionKey is null && limit == 60)
                _recent = result;
            return result.ToArray();
        }
    }
    public RecordedSession[] Sessions()
    {
        lock (_gate)
        {
            if (_sessions is not null)
                return _sessions.ToArray();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT session_key,flags,at,name FROM event_sessions ORDER BY latest DESC LIMIT 20";
            using var reader = cmd.ExecuteReader();
            var result = new List<RecordedSession>();
            while (reader.Read())
                result.Add(new(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3)));
            _sessions = result.ToArray();
            return _sessions.ToArray();
        }
    }
    public string Backup()
    {
        lock (_gate)
            return BackupCore("manuel");
    }
    public string ArchiveRecovery(bool discard = false)
    {
        lock (_gate)
        {
            var records = HueOutput.RecoveryKeys.Append("recovery_bridge").ToDictionary(key => key, key => Get<JsonElement?>(key));
            var directory = Path.Combine(DirectoryPath, "recovery");
            PrivateFiles.Directory(directory);
            var path = Path.Combine(directory, $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.json");
            PrivateFiles.Write(path, JsonSerializer.Serialize(records, JsonDefaults.Options));
            if (discard)
            {
                using var transaction = _db.BeginTransaction();
                foreach (var key in records.Keys)
                {
                    using var cmd = _db.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandText = "DELETE FROM kv WHERE key=$key";
                    cmd.Parameters.AddWithValue("$key", key);
                    cmd.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            return path;
        }
    }
    // Offline operation: the profile lease excludes another running service.
    // Callers must close this Store after replacement and reopen to migrate.
    public string RestoreBackup(string source)
    {
        lock (_gate)
        {
            using var backup = OpenBackup(source, DirectoryPath);
            var previous = BackupCore("avant-restauration");
            backup.BackupDatabase(_db);
            return previous;
        }
    }
    private static SqliteConnection OpenBackup(string source, string directory)
    {
        if (Path.GetFullPath(source) == Path.Combine(directory, "f1-hue.sqlite3"))
            throw new ArgumentException("Choisis un fichier de sauvegarde distinct.");
        var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(source), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        try
        {
            backup.Open();
            using var check = backup.CreateCommand();
            check.CommandText = "PRAGMA integrity_check";
            if (check.ExecuteScalar() as string != "ok")
                throw new InvalidOperationException("La sauvegarde est endommagée.");
            check.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt32(check.ExecuteScalar());
            if (version is < 1 or > DatabaseVersion)
                throw new InvalidOperationException("Format de sauvegarde incompatible.");
            check.CommandText = "SELECT value FROM kv WHERE key='settings'";
            if (check.ExecuteScalar() is string settings)
                JsonSerializer.Deserialize<AppSettings>(settings, JsonDefaults.Options)!.Validate();
            check.CommandText = "SELECT body FROM events LIMIT 1";
            check.ExecuteScalar();
            return backup;
        }
        catch { backup.Dispose(); throw; }
    }
    public static string RestoreProfileBackup(string directory, string source)
    {
        directory = Path.GetFullPath(directory);
        using var backup = OpenBackup(source, directory);
        PrivateFiles.Directory(directory);
        using var lease = new ProfileLease(directory);
        var target = Path.Combine(directory, "f1-hue.sqlite3");
        using var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString());
        database.Open();
        PrivateFiles.Protect(target);
        var backups = Path.Combine(directory, "backups");
        PrivateFiles.Directory(backups);
        var previous = Path.Combine(backups, $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-avant-restauration-{Guid.NewGuid():N}.sqlite3");
        using (var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = previous, Pooling = false }.ToString()))
        {
            copy.Open();
            PrivateFiles.Protect(previous);
            database.BackupDatabase(copy);
        }
        // This explicit offline command can replace a future-version profile,
        // while normal startup must never open it for application writes.
        backup.BackupDatabase(database);
        return previous;
    }
    private string BackupCore(string reason)
    {
        var directory = Path.Combine(DirectoryPath, "backups");
        PrivateFiles.Directory(directory);
        var path = Path.Combine(directory, $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{reason}-{Guid.NewGuid():N}.sqlite3");
        try
        {
            using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            backup.Open();
            PrivateFiles.Protect(path);
            _db.BackupDatabase(backup);
            return path;
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            _db.Dispose();
            _lease.Dispose();
        }
    }
}
