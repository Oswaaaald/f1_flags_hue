using System.Diagnostics;
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
            var writes = new List<(long At, string Alert)>();
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
                    writes.Add((Stopwatch.GetTimestamp(), data.GetProperty("alert").GetString()!));
                    body = "[{\"success\":true}]";
                }
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent(body)
                };
            }))
            {
                BaseAddress = new Uri("https://192.168.1.20/")
            });
            var settings = new AppSettings();
            foreach (var lights in new[] { new[] { "1", "2" }, new[] { "2", "3" } })
            {
                var pulse = new HueNativePulse(client, store, time: new FastClock());
                writes.Clear();
                await pulse.StartAsync(lights, RaceFlag.BLUE, settings.Effects[RaceFlag.BLUE], settings, CancellationToken.None);
                for (var i = 0; writes.Count < 2 && i < 200; i++)
                    await Task.Delay(10);
                check(writes.Count >= 2 && Stopwatch.GetElapsedTime(writes[0].At, writes[1].At).TotalMilliseconds >= 990,
                    "Native group pulses share a one-command-per-second budget");
                var stopping = Stopwatch.GetTimestamp();
                await pulse.StopAsync(CancellationToken.None);
                check(Stopwatch.GetElapsedTime(stopping).TotalMilliseconds < 300 && writes.Last().Alert == "none",
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
    private sealed class FastClock : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => System.CreateTimer(callback, state, dueTime / 100, period == Timeout.InfiniteTimeSpan ? period : period / 100);
    }
}
