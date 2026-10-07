using System.Text.Json;
using Microsoft.Data.Sqlite;
using F1Hue.Core;

namespace F1Hue.Infrastructure;

public sealed class Store : ISettingsStore, IDisposable
{
    private readonly SqliteConnection _db;
    private readonly ProfileLease _lease;
    private readonly object _gate = new();
    public string DirectoryPath { get; }
    public Store(string directory)
    {
        DirectoryPath = Path.GetFullPath(directory); PrivateFiles.Directory(DirectoryPath);
        _lease = new ProfileLease(DirectoryPath);
        var path = Path.Combine(DirectoryPath, "f1-hue.sqlite3");
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        try
        {
            _db.Open();
            Execute("PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; CREATE TABLE IF NOT EXISTS kv (key TEXT PRIMARY KEY, value TEXT NOT NULL); CREATE TABLE IF NOT EXISTS events (id INTEGER PRIMARY KEY, session_key TEXT, received_at TEXT NOT NULL, body TEXT NOT NULL); CREATE INDEX IF NOT EXISTS events_session ON events(session_key, id); PRAGMA user_version=1;");
            PrivateFiles.Protect(path);
        }
        catch { _db.Dispose(); _lease.Dispose(); throw; }
    }
    private void Execute(string sql) { using var cmd = _db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
    public T? Get<T>(string key)
    {
        lock (_gate) { using var cmd = _db.CreateCommand(); cmd.CommandText = "SELECT value FROM kv WHERE key=$key"; cmd.Parameters.AddWithValue("$key", key); var value = cmd.ExecuteScalar(); return value is string text ? JsonSerializer.Deserialize<T>(text, JsonDefaults.Options) : default; }
    }
    public void Put<T>(string key, T value)
    {
        lock (_gate) { using var cmd = _db.CreateCommand(); cmd.CommandText = "INSERT INTO kv(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value"; cmd.Parameters.AddWithValue("$key", key); cmd.Parameters.AddWithValue("$value", JsonSerializer.Serialize(value, JsonDefaults.Options)); cmd.ExecuteNonQuery(); }
    }
    public void Delete(string key) { lock (_gate) { using var cmd = _db.CreateCommand(); cmd.CommandText = "DELETE FROM kv WHERE key=$key"; cmd.Parameters.AddWithValue("$key", key); cmd.ExecuteNonQuery(); } }
    public AppSettings Read() => (Get<AppSettings>("settings") ?? new()).Validate();
    public void Save(AppSettings settings) => Put("settings", settings.Validate());
    public void Append(RaceEvent item)
    {
        if (item.Kind is not ("flag" or "lap" or "session_status") || item.Initial) return;
        lock (_gate)
        {
            using var cmd = _db.CreateCommand(); cmd.CommandText = "INSERT INTO events(session_key,received_at,body) VALUES($session,$at,$body)";
            cmd.Parameters.AddWithValue("$session", (object?)item.SessionKey ?? DBNull.Value); cmd.Parameters.AddWithValue("$at", item.ReceivedAt.ToString("O")); cmd.Parameters.AddWithValue("$body", JsonSerializer.Serialize(item, JsonDefaults.Options)); cmd.ExecuteNonQuery();
            // A home installation retains at most 100,000 derived events.
            Execute("DELETE FROM events WHERE id < (SELECT COALESCE(MAX(id),0)-100000 FROM events)");
        }
    }
    public RaceEvent[] Events(string? sessionKey = null, int limit = 100)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand(); cmd.CommandText = "SELECT body FROM events WHERE ($session IS NULL OR session_key=$session) ORDER BY id DESC LIMIT $limit";
            cmd.Parameters.AddWithValue("$session", (object?)sessionKey ?? DBNull.Value); cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100000));
            using var reader = cmd.ExecuteReader(); var items = new List<RaceEvent>(); while (reader.Read()) items.Add(JsonSerializer.Deserialize<RaceEvent>(reader.GetString(0), JsonDefaults.Options)!); return items.ToArray();
        }
    }
    public object[] Sessions()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "WITH sessions AS (SELECT session_key,COUNT(*) AS flags,MAX(id) AS latest FROM events WHERE session_key IS NOT NULL AND json_extract(body,'$.kind')='flag' GROUP BY session_key ORDER BY latest DESC LIMIT 20) SELECT s.session_key,s.flags,e.received_at,json_extract(e.body,'$.sessionName') FROM sessions s JOIN events e ON e.id=s.latest ORDER BY s.latest DESC";
            using var reader = cmd.ExecuteReader(); var result = new List<object>();
            while (reader.Read()) result.Add(new { key = reader.GetString(0), flags = reader.GetInt32(1), at = reader.GetString(2), name = reader.IsDBNull(3) ? "Séance F1" : reader.GetString(3) });
            return result.ToArray();
        }
    }
    public void Dispose() { lock (_gate) { _db.Dispose(); _lease.Dispose(); } }
}
