using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using F1Hue.Core;
using F1Hue.Infrastructure;

static class NativePulseTests
{
    private const string A = "11111111-1111-1111-1111-111111111111", B = "22222222-2222-2222-2222-222222222222";
    public static async Task Run(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "f1hue-native-" + Guid.NewGuid());
        try
        {
            using var store = new Store(directory);
            var bridge = new Bridge();
            var client = bridge.Client();
            var settings = new AppSettings { LightIds = [A, B], EntertainmentAreaId = Guid.NewGuid().ToString() };
            var output = new HueOutput(client, store, new FastClock());
            await output.CaptureAsync(settings, CancellationToken.None);
            await using (var engine = new EffectEngine(output))
            {
                await engine.PlayAsync(RaceFlag.SC, settings);
                await Until(() => bridge.Count("lselect") >= 2);
                check(bridge.Calls.Where(c => c.Body.Contains("lselect")).All(c => c.Path == "groups/7/action")
                    && !bridge.Calls.Any(c => c.Path.Contains("entertainment") || c.Body.Contains("signaling")),
                    "Native SC uses Python's lselect on the exact group, without Entertainment or v2 signaling");
                var start = bridge.Calls.First(c => c.Body.Contains("lselect"));
                var payload = JsonSerializer.Deserialize<JsonElement>(start.Body);
                check(payload.GetProperty("transitiontime").GetInt32() == 0 && payload.GetProperty("effect").GetString() == "none"
                    && payload.GetProperty("xy")[0].GetDouble() == settings.Effects[RaceFlag.SC].X,
                    "Native SC sets yellow, clears colorloop and starts the pulse in one command without intermediate color fades");
                await engine.PlayAsync(RaceFlag.RED, settings);
                var calls = bridge.Calls.ToArray();
                var red = Array.FindIndex(calls, c => c.Path == "groups/7/action" && c.Body.Contains("xy")
                    && JsonSerializer.Deserialize<JsonElement>(c.Body).GetProperty("xy")[0].GetDouble() == settings.Effects[RaceFlag.RED].X);
                check(red >= 0 && calls.Take(red).Count(c => c.Method == HttpMethod.Put && c.Path == "groups/7/action"
                        && c.Body == "{\"alert\":\"none\"}") == 1 && !calls.Any(c => c.Path.StartsWith("lights/")),
                    "Changing to red cancels the native pulse in one group command before changing color");
                var count = bridge.Count("lselect"); await Task.Delay(160);
                check(bridge.Count("lselect") == count, "No old SC renewal can overwrite the next flag");
                await engine.StopAsync(true);
                check(store.Get<string[]>("pending_native_alert") is null && store.Get<Dictionary<string, JsonElement>>("pending_restore") is null,
                    "Native Stop removes the pending pulse and restoration markers");
            }

            bridge.Calls.Clear();
            await output.CaptureAsync(settings, CancellationToken.None);
            await using (var engine = new EffectEngine(output))
            {
                await engine.PlayAsync(RaceFlag.SC, settings);
                // The group can change outside this app. Stop must still target the saved lamps.
                bridge.GroupChanged = true; bridge.FailStopOnce = true;
                var failed = false;
                try { await engine.StopAsync(true); } catch (InvalidOperationException) { failed = true; }
                check(failed && store.Get<string[]>("pending_native_alert")!.SequenceEqual(["1"])
                    && store.Get<Dictionary<string, JsonElement>>("pending_restore") is not null,
                    "Failed native stop retains only the failed lamp and keeps its original baseline");
                check(bridge.Calls.Any(c => c.Path == "lights/2/state" && c.Body.Contains("none")),
                    "One rejected cancellation does not prevent stopping the other lamp");
                bridge.Calls.Clear();
                await new HueOutput(client, store).RecoverAsync(CancellationToken.None);
                check(bridge.Calls.First().Path == "lights/1/state" && bridge.Calls.First().Body.Contains("none")
                    && !bridge.Calls.Any(c => c.Path == "lights/2/state" || c.Path == "lights/3/state")
                    && store.Get<string[]>("pending_native_alert") is null && store.Get<Dictionary<string, JsonElement>>("pending_restore") is null,
                    "Restart retries only the pending native cancellation before restoring saved lamps");
            }
            bridge.GroupChanged = false;

            await output.CaptureAsync(settings, CancellationToken.None);
            await output.ApplyAsync(RaceFlag.SC, settings.Effects[RaceFlag.SC], settings, CancellationToken.None);
            bridge.Calls.Clear();
            await new HueOutput(client, store).RecoverAsync(CancellationToken.None);
            check(bridge.Calls.Count(c => c.Method == HttpMethod.Put && c.Path == "groups/7/action" && c.Body == "{\"alert\":\"none\"}") == 1
                && !bridge.Calls.Any(c => c.Path.StartsWith("lights/")) && store.Get<JsonElement?>("pending_native_group") is null,
                "Restart uses the persisted exact group to stop all lamps in one command before restoring");
            await output.EndAnimationAsync(CancellationToken.None); // Dispose the original process's simulated renewal.

            bridge.Calls.Clear(); bridge.BlockRenewal = true;
            var pulse = new HueNativePulse(client, store, time: new FastClock());
            await pulse.StartAsync(["1", "2"], RaceFlag.SC, settings.Effects[RaceFlag.SC], settings, CancellationToken.None);
            await bridge.RenewalEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var stopping = pulse.StopAsync(CancellationToken.None);
            await Task.Delay(40);
            check(!stopping.IsCompleted && !bridge.Calls.Any(c => c.Path.StartsWith("lights/") && c.Body.Contains("none")),
                "Stop waits for an in-flight native renewal before issuing cancellation");
            bridge.ReleaseRenewal.TrySetResult(); await stopping.WaitAsync(TimeSpan.FromSeconds(2));
            var writesAfterStop = bridge.Calls.Count; await Task.Delay(150);
            check(bridge.Calls.Count == writesAfterStop, "No renewal is sent after native Stop returns");
            bridge.BlockRenewal = false;

            bridge.Calls.Clear();
            var changedGroup = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            pulse = new HueNativePulse(client, store, e => changedGroup.TrySetResult(e), new FastClock());
            await pulse.StartAsync(["1", "2"], RaceFlag.SC, settings.Effects[RaceFlag.SC], settings, CancellationToken.None);
            bridge.GroupChanged = true;
            await changedGroup.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await pulse.StopAsync(CancellationToken.None);
            check(bridge.Count("lselect") == 1 && !bridge.Calls.Any(c => c.Path == "lights/3/state"),
                "An externally widened group is detected before renewal and never extends the pulse to another lamp");
            bridge.GroupChanged = false;

            store.Save(settings);
            await using (var runner = new Runner(store, new FakeFeed(), new HueOutput(client, store, new FastClock())))
            {
                for (var run = 0; run < 3; run++)
                {
                    bridge.Calls.Clear();
                    await runner.StartAsync("preview", RaceFlag.SC);
                    await Until(() => bridge.Count("lselect") >= 1);
                    await runner.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
                    check(!runner.State.Running && !runner.State.CleanupPending && runner.State.Error is null
                        && store.Get<string[]>("pending_native_alert") is null,
                        $"Native Safety Car preview {run + 1} starts and stops cleanly through the real runner");
                }
            }

            bridge.Calls.Clear(); bridge.HasExactGroup = false;
            pulse = new HueNativePulse(client, store);
            await pulse.StartAsync(["1", "2"], RaceFlag.VSC, settings.Effects[RaceFlag.VSC], settings, CancellationToken.None);
            check(bridge.Calls.Any(c => c.Method == HttpMethod.Post && c.Path == "groups"
                    && JsonDocument.Parse(c.Body).RootElement.GetProperty("lights").EnumerateArray().Select(l => l.GetString()).SequenceEqual(["1", "2"]))
                && !bridge.Calls.Any(c => c.Path.StartsWith("groups/0") || c.Path.StartsWith("groups/8")),
                "Missing exact group creates one with only the chosen lamps; all-lamp and wider groups are never used");
            await pulse.StopAsync(CancellationToken.None);

            bridge.Calls.Clear();
            await output.CaptureAsync(settings, CancellationToken.None);
            await using (var engine = new EffectEngine(output))
            {
                var rules = settings with { Effects = new(settings.Effects) { [RaceFlag.CHEQUERED] = settings.Effects[RaceFlag.CHEQUERED] with { DurationSeconds = .12 } } };
                await engine.PlayAsync(RaceFlag.CHEQUERED, rules);
                await engine.Completion.WaitAsync(TimeSpan.FromSeconds(2));
                check(engine.Active is null && store.Get<string[]>("pending_native_alert") is null
                    && bridge.Calls.Last().Path.StartsWith("clip/v2/resource/scene/"), "Fixed native pulse duration cancels the pulse and recalls the baseline together");
                await engine.PlayAsync(RaceFlag.SC, settings);
                await engine.PlayAsync(RaceFlag.BLUE, settings); // Disabled by default.
                check(engine.Active is null && store.Get<string[]>("pending_native_alert") is null
                    && bridge.Count("select") == 0, "Disabled blue stops a native SC without playing its own pulse");
            }

            bridge.Calls.Clear();
            pulse = new HueNativePulse(client, store, time: new FastClock());
            await pulse.StartAsync(["1"], RaceFlag.BLUE, settings.Effects[RaceFlag.BLUE], settings, CancellationToken.None);
            await Until(() => bridge.Count("select") >= 2);
            await pulse.StopAsync(CancellationToken.None);
            check(bridge.Calls.All(c => c.Path == "lights/1/state") && bridge.Count("lselect") == 0,
                "Blue uses Python's repeated select pulse and a single lamp needs no group");

            bridge.RejectPulse = true;
            pulse = new HueNativePulse(client, store);
            var rejected = false;
            try { await pulse.StartAsync(["1"], RaceFlag.SC, settings.Effects[RaceFlag.SC], settings, CancellationToken.None); }
            catch (InvalidOperationException e) { rejected = !e.Message.Contains("private-test-key") && e.Message.Contains("refusée"); }
            check(rejected && store.Get<string[]>("pending_native_alert")!.SequenceEqual(["1"]),
                "Hue v1 errors inside HTTP 200 are surfaced with credentials masked and recovery preserved");
            await pulse.StopAsync(CancellationToken.None);
            var invalid = false;
            try { await client.LegacyRequestAsync(HttpMethod.Put, "groups/0/action", new { alert = "lselect" }, CancellationToken.None); }
            catch (ArgumentException) { invalid = true; }
            check(invalid, "Legacy API refuses the all-lamps group before sending any request");
        }
        finally { Directory.Delete(directory, true); }
    }
    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        if (!condition()) throw new Exception("Native test timed out");
    }
    private sealed class FastClock : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => System.CreateTimer(callback, state, dueTime / 100, period == Timeout.InfiniteTimeSpan ? period : period / 100);
    }
    private sealed class Bridge
    {
        public readonly ConcurrentQueue<(HttpMethod Method, string Path, string Body)> Calls = new();
        public bool HasExactGroup = true, GroupChanged, FailStopOnce, BlockRenewal, RejectPulse;
        private readonly SceneBridge Scenes = new();
        public readonly TaskCompletionSource RenewalEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseRenewal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count(string alert) => Calls.Count(c => c.Body.Contains("\"alert\":\"" + alert + "\""));
        public HueClient Client() => new(new MemoryVault(), (_, pin, _) =>
        {
            if (pin != "test-pin") throw new Exception("Native requests must retain certificate pinning");
            return new HttpClient(new Handler(Reply)) { BaseAddress = new Uri("https://192.168.1.20/") };
        });
        private async Task<HttpResponseMessage> Reply(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/').Replace("api/private-test-key/", "", StringComparison.Ordinal);
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Enqueue((request.Method, path, body));
            string response;
            if (path.StartsWith("clip/"))
                response = Scenes.Reply(path, request.Method, body, [Light(A, "/lights/1"), Light(B, "/lights/2"), Light(Guid.NewGuid().ToString(), "/lights/3")]);
            else if (path == "groups" && request.Method == HttpMethod.Get)
                response = HasExactGroup ? "{\"0\":{\"lights\":[\"1\",\"2\"]},\"8\":{\"lights\":[\"1\",\"2\",\"3\"]},\"7\":{\"lights\":[\"1\",\"2\"]}}" : "{\"0\":{\"lights\":[\"1\",\"2\"]},\"8\":{\"lights\":[\"1\",\"2\",\"3\"]}}";
            else if (path == "groups" && request.Method == HttpMethod.Post) { HasExactGroup = true; response = "[{\"success\":{\"id\":\"7\"}}]"; }
            else if (path == "groups/7") response = GroupChanged ? "{\"lights\":[\"1\",\"2\",\"3\"]}" : "{\"lights\":[\"1\",\"2\"]}";
            else if (FailStopOnce && path == "lights/1/state" && body.Contains("none"))
            { FailStopOnce = false; response = "[{\"error\":{\"type\":901,\"description\":\"temporarily unavailable\"}}]"; }
            else if (RejectPulse && body.Contains("lselect")) response = "[{\"error\":{\"type\":7,\"description\":\"invalid alert /api/private-test-key/\"}}]";
            else response = "[{\"success\":{\"alert\":true}}]";
            if (BlockRenewal && body.Contains("lselect") && Count("lselect") >= 2)
            { RenewalEntered.TrySetResult(); await ReleaseRenewal.Task; } // Bridge applied it; reply ignores cancellation.
            return new(HttpStatusCode.OK) { Content = new StringContent(response, System.Text.Encoding.UTF8, "application/json") };
        }
        private static object Light(string id, string legacy) => new { id, type = "light", owner = new { rid = SceneBridge.Device(id), rtype = "device" }, id_v1 = legacy, on = new { on = true }, dimming = new { brightness = 42 }, color = new { xy = new { x = .3, y = .4 } } };
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
}
