using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using F1Hue.Core;
using F1Hue.Infrastructure;

internal static class HueRecoveryAuditTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "f1hue-recovery-audit-" + Guid.NewGuid());
        try
        {
            using var store = new Store(directory);
            var groups = new Dictionary<string, JsonElement>();
            var budgetClock = new BudgetClock();
            var writes = new ConcurrentQueue<(long At, string Alert)>();
            var deleted = new List<string>();
            var lostCreation = false;
            var next = 40;
            using var client = new HueClient(new MemoryVault(), (_, _, _) => new HttpClient(new ReplyHandler(request =>
            {
                var route = request.RequestUri!.AbsolutePath.Split("/api/private-test-key/").Last();
                string body;
                if (request.Method == HttpMethod.Post)
                {
                    groups[(++next).ToString()] = JsonSerializer.Deserialize<JsonElement>(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                    body = lostCreation ? "[{\"error\":{\"description\":\"lost reply\"}}]" : "[{\"success\":{\"id\":\"" + next + "\"}}]";
                }
                else if (route == "groups")
                    body = JsonSerializer.Serialize(groups);
                else if (request.Method == HttpMethod.Get)
                    body = groups[route.Split('/')[1]].GetRawText();
                else if (request.Method == HttpMethod.Delete)
                {
                    var id = route.Split('/')[1];
                    groups.Remove(id);
                    deleted.Add(id);
                    body = "[{\"success\":true}]";
                }
                else
                {
                    var data = JsonSerializer.Deserialize<JsonElement>(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                    writes.Enqueue((budgetClock.GetTimestamp(), data.GetProperty("alert").GetString()!));
                    body = "[{\"success\":true}]";
                }
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent(body)
                };
            }))
            {
                BaseAddress = new Uri("https://192.168.1.20/")
            }, budgetClock);
            var settings = new AppSettings();
            foreach (var lights in new[] { new[] { "1", "2" }, new[] { "2", "3" } })
            {
                var pulse = new HueNativePulse(client, store, time: new FastClock());
                writes.Clear();
                budgetClock.Advance(TimeSpan.FromSeconds(1));
                await pulse.StartAsync(lights, RaceFlag.BLUE, settings.Effects[RaceFlag.BLUE], settings, CancellationToken.None);
                await WaitUntil(() => budgetClock.Pending == 1);
                budgetClock.Advance(TimeSpan.FromMilliseconds(999));
                var withheld = writes.Count == 1;
                budgetClock.Advance(TimeSpan.FromMilliseconds(1));
                await WaitUntil(() => writes.Count == 2 && budgetClock.Pending == 1);
                var sent = writes.ToArray();
                check(withheld && budgetClock.GetElapsedTime(sent[0].At, sent[1].At) == TimeSpan.FromSeconds(1),
                    "Native group pulses share a one-command-per-second budget");
                // The next renewal is now blocked on a timer that will never
                // advance. Stop must cancel it and bypass the budget entirely.
                await pulse.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                check(budgetClock.Pending == 0 && writes.Count == 3 && writes.Last().Alert == "none",
                    "Stop cancels waiting renewal and bypasses the ordinary group budget");
                await new HueOutput(client, store).ReleaseAsync(CancellationToken.None);
                check(groups.Count == 0 && store.Get<JsonElement?>("hue_owned_group") is null, "Changing selections leaves no application-owned fallback group behind");
            }
            lostCreation = true;
            try
            {
                await new HueNativePulse(client, store).StartAsync(["1", "2"], RaceFlag.SC, settings.Effects[RaceFlag.SC], settings, CancellationToken.None);
            }
            catch (InvalidOperationException) { }
            check(groups.Count == 1 && store.Get<JsonElement?>("hue_owned_group") is not null, "A lost group-creation response retains an ownership token");
            await new HueOutput(client, store).RecoverAsync(CancellationToken.None);
            check(groups.Count == 0 && deleted.Count == 3, "Startup finds and removes its orphaned native group after a lost response");

            var existingVault = new MemoryVault();
            var posts = 0;
            using var wrongBridge = new HueClient(existingVault, (_, _, capture) =>
            {
                capture?.Invoke("new-pin");
                return new HttpClient(new ReplyHandler(request =>
                {
                    if (request.Method == HttpMethod.Post)
                        posts++;
                    return new(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"bridgeid\":\"different-bridge\",\"name\":\"Foreign\"}")
                    };
                }))
                {
                    BaseAddress = new Uri("https://192.168.1.20/")
                };
            });
            try
            {
                await wrongBridge.PairAsync("192.168.1.20", CancellationToken.None, "expected-bridge");
            }
            catch (InvalidOperationException) { }
            check(posts == 0 && existingVault.Read()!.ApplicationKey == "private-test-key", "Recovery pairing refuses another bridge before issuing a new key or replacing the vault");

            const string a = "11111111-1111-1111-1111-111111111111", b = "22222222-2222-2222-2222-222222222222";
            var state = JsonSerializer.Deserialize<JsonElement>("{\"on\":{\"on\":true},\"color\":{\"xy\":{\"x\":0.3,\"y\":0.4}}}");
            store.Put("pending_restore", new Dictionary<string, JsonElement> { [a] = state, [b] = state });
            var changed = new List<string>();
            using var remaining = new HueClient(new MemoryVault(), (_, _, _) => new HttpClient(new ReplyHandler(request =>
            {
                if (request.Method == HttpMethod.Put)
                    changed.Add(request.RequestUri!.AbsolutePath);
                var data = request.Method == HttpMethod.Get ? JsonSerializer.Serialize(new[] { new { id = a, on = state.GetProperty("on"), color = state.GetProperty("color") } }) : "[]";
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"errors\":[],\"data\":" + data + "}")
                };
            }))
            {
                BaseAddress = new Uri("https://192.168.1.20/")
            });
            var partial = new HueOutput(remaining, store, new FastClock());
            await partial.RestoreAvailableAsync(CancellationToken.None);
            check(changed.Count == 1 && changed[0].EndsWith(a) && store.Get<Dictionary<string, JsonElement>>("pending_restore")!.Keys.SequenceEqual([b]),
                "Partial recovery restores only the available original lamp and retains the missing lamp's baseline");
            check(Directory.GetFiles(Path.Combine(directory, "recovery")).Any(file => File.ReadAllText(file).Contains(a) && File.ReadAllText(file).Contains(b)),
                "Partial recovery keeps a private archive of the complete original ambiance");
        }
        finally { Directory.Delete(directory, true); }
    }
    private static async Task WaitUntil(Func<bool> ready)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready())
            await Task.Delay(10, deadline.Token);
    }
    // Only the command budget uses this clock. Advancing it is explicit, so a
    // slow filesystem or a busy CI machine cannot satisfy or fail the ordering.
    private sealed class BudgetClock : TimeProvider
    {
        private long _ticks;
        private readonly ConcurrentQueue<BudgetTimer> _timers = new();
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public int Pending => _timers.Count(timer => timer.Active);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new BudgetTimer(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Enqueue(timer);
            return timer;
        }
        public void Advance(TimeSpan duration)
        {
            var now = Interlocked.Add(ref _ticks, duration.Ticks);
            foreach (var timer in _timers)
                timer.FireIfDue(now);
        }
        private sealed class BudgetTimer(BudgetClock owner, TimerCallback callback, object? state) : ITimer
        {
            private long _due;
            private int _disposed;
            public bool Active => Volatile.Read(ref _disposed) == 0;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (period != Timeout.InfiniteTimeSpan)
                    throw new NotSupportedException("Only one-shot budget timers are expected.");
                Interlocked.Exchange(ref _due, dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.GetTimestamp() + dueTime.Ticks);
                return Active;
            }
            public void FireIfDue(long now)
            {
                if (now >= Interlocked.Read(ref _due) && Interlocked.Exchange(ref _disposed, 1) == 0)
                    callback(state);
            }
            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
    private sealed class FastClock : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => System.CreateTimer(callback, state, dueTime / 100, period == Timeout.InfiniteTimeSpan ? period : period / 100);
    }
}
