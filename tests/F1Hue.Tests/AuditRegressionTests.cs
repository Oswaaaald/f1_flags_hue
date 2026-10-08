using System.Diagnostics;
using System.Net;
using System.Text.Json;
using F1Hue.Core;
using F1Hue.Infrastructure;
using Microsoft.Data.Sqlite;

internal static class AuditRegressionTests
{
    private const string Light = "11111111-1111-1111-1111-111111111111";
    private static JsonElement J(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    private static async Task<bool> Rejected(Func<Task> action)
    {
        try
        {
            await action();
            return false;
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException) { return true; }
    }
    private static void Sql(string directory, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "f1-hue.sqlite3"), Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    public static async Task Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "f1hue-audit-regressions-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var directory = Path.Combine(root, "store");
            string savedBackup;
            using (var store = new Store(directory))
            {
                store.Save(new()
                {
                    OffsetSeconds = 41.5,
                    LightIds = [Light]
                });
                var revision = store.Read().Revision;
                store.Save(store.Read() with
                {
                    Brightness = 150
                }, revision);
                check(await Rejected(() => { store.Save(store.Read() with { OffsetSeconds = 0 }, revision); return Task.CompletedTask; })
                    && store.Read().OffsetSeconds == 41.5, "Concurrent settings edit is rejected without overwriting the first save");
                var copy = store.Read();
                copy.LightIds[0] = "invalid";
                copy.Effects.Clear();
                check(store.Read().LightIds.Single() == Light && store.Read().Effects.Count == 9, "Cached settings cannot be mutated by a reader");
                var backup = store.Backup();
                savedBackup = backup;
                store.Save(store.Read() with
                {
                    OffsetSeconds = 7
                });
                var previous = store.RestoreBackup(backup);
                check(File.Exists(previous), "Offline restoration first creates a rollback backup");
            }
            using (var reopened = new Store(directory))
                check(reopened.Read().OffsetSeconds == 41.5, "Backup restoration preserves selection and calibration across reopening");
            Sql(directory, "PRAGMA user_version=99");
            check(await Rejected(() => { using var unknown = new Store(directory); return Task.CompletedTask; }), "A future database version is refused before migration");
            using (var connection = new SqliteConnection("Data Source=" + Path.Combine(directory, "f1-hue.sqlite3")))
            {
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "PRAGMA user_version";
                check(Convert.ToInt32(cmd.ExecuteScalar()) == 99, "Refusing a future database does not relabel its schema");
            }
            check(await Rejected(() => { new AppSettings { SchemaVersion = 99 }.Validate(); return Task.CompletedTask; }), "Future settings schemas are refused");
            var rollback = Store.RestoreProfileBackup(directory, savedBackup);
            using (var compatible = new Store(directory))
                check(File.Exists(rollback) && compatible.Read().OffsetSeconds == 41.5, "Explicit offline restoration can recover a future-version profile without relabelling it on startup");

            var old = Path.Combine(root, "version-one");
            Directory.CreateDirectory(old);
            Sql(old, "CREATE TABLE kv(key TEXT PRIMARY KEY,value TEXT NOT NULL); CREATE TABLE events(id INTEGER PRIMARY KEY,session_key TEXT,received_at TEXT NOT NULL,body TEXT NOT NULL); PRAGMA user_version=1;");
            Sql(old, "INSERT INTO kv VALUES('settings','{\"offsetSeconds\":40}'); INSERT INTO events VALUES(1,'race','2026-10-08T00:00:00Z','{\"kind\":\"flag\",\"value\":\"RED\",\"sessionName\":\"Monza\",\"receivedAt\":\"2026-10-08T00:00:00Z\"}');");
            using (var migrated = new Store(old))
                check(migrated.Read().OffsetSeconds == 40 && migrated.Sessions().Single().Flags == 1 && migrated.Sessions().Single().Name == "Monza"
                    && Directory.GetFiles(Path.Combine(old, "backups")).Length == 1, "Version-one migration preserves settings/history and backs up before altering tables");
            var invalid = Path.Combine(root, "interrupted");
            Directory.CreateDirectory(invalid);
            Sql(invalid, "CREATE TABLE kv(key TEXT PRIMARY KEY,value TEXT NOT NULL); CREATE TABLE events(id INTEGER PRIMARY KEY,session_key TEXT,received_at TEXT NOT NULL,body TEXT NOT NULL); PRAGMA user_version=1; INSERT INTO events VALUES(1,'race','today','not-json');");
            try
            {
                using var broken = new Store(invalid);
            }
            catch (SqliteException) { }
            using (var connection = new SqliteConnection("Data Source=" + Path.Combine(invalid, "f1-hue.sqlite3")))
            {
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('events') WHERE name='kind'";
                check(Convert.ToInt32(cmd.ExecuteScalar()) == 0, "An interrupted migration rolls back schema alterations");
            }

            var journal = Path.Combine(root, "journal");
            using (var store = new Store(journal))
            {
                var feed = new F1Feed(store);
                var delivered = new List<RaceEvent>();
                feed.Event += delivered.Add;
                feed.Simulate("SessionStatus", J("{\"Status\":\"Started\"}"));
                Sql(journal, "CREATE TRIGGER reject_events BEFORE INSERT ON events BEGIN SELECT RAISE(ABORT,'disk full test'); END;");
                feed.Simulate("TrackStatus", J("{\"Status\":\"5\"}"));
                check(delivered.Any(e => e.Value == "RED") && feed.State.JournalError is not null, "A journal write failure still delivers RED and exposes a degraded state");
                Sql(journal, "DROP TRIGGER reject_events");
                feed.Simulate("TrackStatus", J("{\"Status\":\"5\"}"));
                check(delivered.Count(e => e.Value == "RED") == 1, "A repeated RED after disk recovery is not lost or replayed twice");
                feed.Simulate("TrackStatus", J("{\"Status\":\"1\"}"));
                check(feed.State.JournalError is null && store.Events().Any(e => e.Value == "GREEN"), "The journal resumes and clears its warning after a successful write");
            }

            var largeJournal = Path.Combine(root, "large-journal");
            using (var store = new Store(largeJournal))
            {
                Sql(largeJournal, """
                    WITH RECURSIVE rows(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM rows WHERE n<100000)
                    INSERT INTO events(session_key,received_at,body,kind)
                    SELECT 'race','2026-10-08T00:00:00Z',json_object('kind',CASE WHEN n%2=1 THEN 'flag' ELSE 'lap' END,'value','RED','receivedAt','2026-10-08T00:00:00Z','sessionKey','race','sessionName','Large race'),CASE WHEN n%2=1 THEN 'flag' ELSE 'lap' END FROM rows;
                    """);
                check(store.Sessions().Single().Flags == 50000, "Indexed session projection counts flags separately in a 100,000-event journal");
                store.Events(limit: 60);
                var watch = Stopwatch.StartNew();
                for (var i = 0; i < 1000; i++)
                {
                    store.Read();
                    store.Events(limit: 60);
                    store.Sessions();
                }
                Console.WriteLine($"MEASURE 1000 cached settings/journal/session projections with 100,000 rows: {watch.Elapsed.TotalMilliseconds:F1} ms");
                var feed = new F1Feed(store);
                feed.Simulate("SessionInfo", J("{\"Key\":\"race\",\"Name\":\"Large race\"}"));
                feed.Simulate("SessionStatus", J("{\"Status\":\"Started\"}"));
                feed.Simulate("TrackStatus", J("{\"Status\":\"1\"}"));
                check(store.Sessions().Sum(s => s.Flags) == 50000 && store.Events(limit: 60)[0].Value == "GREEN",
                    "Journal pruning and a new flag invalidate caches without losing the indexed session count");
            }

            foreach (var item in new[] { ("4", "SAFETY CAR IN THIS LAP", "SC_ENDING"), ("6", "VIRTUAL SAFETY CAR ENDING", "VSC_ENDING") })
            {
                var parser = new F1Parser();
                var snapshot = JsonSerializer.SerializeToElement(new
                {
                    SessionStatus = new
                    {
                        Status = "Started"
                    },
                    TrackStatus = new
                    {
                        Status = item.Item1
                    },
                    RaceControlMessages = new
                    {
                        _kf = true,
                        Messages = new[] { new { Message = item.Item2, Category = "SafetyCar" } }
                    }
                });
                var events = parser.Snapshot(snapshot);
                check(parser.State.LastFlag == item.Item3 && events.Count(e => e.Kind == "flag") == 1 && events.All(e => e.Initial), "A reconnect snapshot restores " + item.Item3 + " without replaying historical flags");
            }
            var reordered = new F1Parser();
            var subscription = new F1SubscriptionBuffer(data => reordered.Snapshot(data), (topic, data) => reordered.Update(topic, data));
            subscription.Receive("TrackStatus", J("{\"Status\":\"5\"}"));
            subscription.Complete(J("{\"SessionStatus\":{\"Status\":\"Started\"},\"TrackStatus\":{\"Status\":\"1\"}}"));
            check(reordered.State.LastFlag == "RED", "A delta received during Subscribe is applied after its older keyframe");

            using (var automatic = new AutomaticStartup(new FastClock()))
            {
                var attempts = 0;
                await automatic.RunAsync(_ => ++attempts == 1 ? throw new IOException("Wi-Fi absent") : Task.CompletedTask, () => true, CancellationToken.None);
                check(attempts == 2 && automatic.State.Phase == "ready", "Automatic live startup retries after the bridge network becomes available");
            }
            using (var automatic = new AutomaticStartup())
            {
                var attempts = 0;
                var task = automatic.RunAsync(_ => { attempts++; throw new IOException("offline"); }, () => true, CancellationToken.None);
                automatic.Cancel();
                await task;
                check(attempts == 1 && automatic.State.Phase == "idle", "Manual Stop cancels automatic startup retries");
            }
            using (var store = new Store(Path.Combine(root, "prepare")))
            {
                store.Save(new()
                {
                    LightIds = [Light]
                });
                await using var runner = new Runner(store, new FakeFeed(), new OfflineOutput());
                check(await Rejected(() => runner.StartAsync("live")) && !runner.State.CleanupPending, "A failed preparation without a light command does not invent pending recovery");
            }

            using (var store = new Store(Path.Combine(root, "single")))
            {
                var acceptedWrites = 0;
                var obey = false;
                var reads = 0;
                var desired = J("{\"on\":{\"on\":true},\"color\":{\"xy\":{\"x\":0.3,\"y\":0.4}}}");
                store.Put("pending_restore", new Dictionary<string, JsonElement> { [Light] = desired });
                using var client = new HueClient(new MemoryVault(), (_, _, _) => new HttpClient(new ReplyHandler(request =>
                {
                    if (request.Method == HttpMethod.Put)
                        acceptedWrites++;
                    else
                        reads++;
                    var state = obey ? desired : J("{\"on\":{\"on\":true},\"color\":{\"xy\":{\"x\":0.17,\"y\":0.7}}}");
                    var data = request.Method == HttpMethod.Get ? JsonSerializer.Serialize(new[] { new { id = Light, on = state.GetProperty("on"), color = state.GetProperty("color") } }) : "[]";
                    return new(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"errors\":[],\"data\":" + data + "}")
                    };
                }))
                {
                    BaseAddress = new Uri("https://192.168.1.20/")
                });
                var output = new HueOutput(client, store, new FastClock());
                check(await Rejected(() => output.RecoverAsync(CancellationToken.None)) && acceptedWrites == 3 && store.Get<JsonElement?>("pending_restore") is not null,
                    "Three accepted but ineffective single-lamp restores retain the original ambiance");
                obey = true;
                reads = 0;
                await output.RecoverAsync(CancellationToken.None);
                check(reads == 2 && !output.RecoveryPending, "Single-lamp recovery completes only after two settled-state confirmations");
                store.Put("pending_restore", new Dictionary<string, JsonElement> { [Light] = desired });
                var archive = await output.AbandonRecoveryAsync(CancellationToken.None);
                check(File.Exists(archive) && !output.RecoveryPending && File.ReadAllText(archive).Contains(Light), "Explicit recovery abandonment archives the original state before clearing markers");
            }
            check(await Rejected(() => { HueOutput.ValidateRestorationCapability(J("{\"dynamics\":{\"status\":\"dynamic_palette\"}}")); return Task.CompletedTask; }), "Dynamic palettes are rejected before capture rather than silently frozen on restoration");
            check(await Rejected(() => { HueOutput.ValidateRestorationCapability(J("{\"effects_v2\":{\"status\":{\"effect\":\"candle\",\"parameters\":{\"speed\":0.8}}}}")); return Task.CompletedTask; }), "Unrestorable effects_v2 parameters are detected before changing the lamps");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    private sealed class FastClock : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => System.CreateTimer(callback, state, dueTime / 100, period == Timeout.InfiniteTimeSpan ? period : period / 100);
    }
    private sealed class OfflineOutput : IEffectOutput
    {
        public Task CaptureAsync(AppSettings settings, CancellationToken ct) => throw new InvalidOperationException("offline");
        public Task ApplyAsync(RaceFlag flag, EffectSpec effect, AppSettings settings, CancellationToken ct) => Task.CompletedTask;
        public Task EndAnimationAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RestoreAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
