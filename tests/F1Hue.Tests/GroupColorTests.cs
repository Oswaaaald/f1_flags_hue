using System.Net;
using System.Text.Json;
using F1Hue.Core;
using F1Hue.Infrastructure;

internal static class GroupColorTests
{
    private const string A = "11111111-1111-1111-1111-111111111111", B = "22222222-2222-2222-2222-222222222222";
    public static async Task Run(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "f1hue-group-colors-" + Guid.NewGuid());
        try
        {
            using var store = new Store(directory);
            var bridge = new Bridge();
            var output = new HueOutput(bridge.Client(), store);
            var settings = new AppSettings { LightIds = [A, B], Brightness = 180, TransitionSeconds = .7 };
            await output.CaptureAsync(settings, CancellationToken.None);
            var lookups = 0;
            var creations = 0;
            foreach (var flag in new[] { RaceFlag.GREEN, RaceFlag.YELLOW, RaceFlag.RED, RaceFlag.SC_ENDING, RaceFlag.VSC_ENDING })
            {
                bridge.Calls.Clear();
                await output.ApplyAsync(flag, settings.Effects[flag], settings, CancellationToken.None);
                var writes = bridge.Calls.Where(c => c.Method == HttpMethod.Put).ToArray();
                lookups += bridge.Calls.Count(c => c.Path == "groups" && c.Method == HttpMethod.Get);
                creations += bridge.Calls.Count(c => c.Method == HttpMethod.Post);
                var body = JsonSerializer.Deserialize<JsonElement>(writes.Single().Body);
                check(writes.Single().Path == "groups/7/action" && body.GetProperty("on").GetBoolean()
                    && body.GetProperty("bri").GetInt32() == 180 && body.GetProperty("transitiontime").GetInt32() == 7
                    && body.GetProperty("xy")[0].GetDouble() == settings.Effects[flag].X
                    && body.GetProperty("xy")[1].GetDouble() == settings.Effects[flag].Y,
                    $"Solid {flag} sends one exact-group command for all chosen lamps, brightness and fade");
            }
            check(lookups == 1 && creations == 0, "An existing exact color group is reused without changing a room");

            bridge.Calls.Clear();
            bridge.GroupChanged = true;
            var refused = false;
            try
            {
                await output.ApplyAsync(RaceFlag.GREEN, settings.Effects[RaceFlag.GREEN], settings, CancellationToken.None);
            }
            catch (InvalidOperationException e) { refused = e.Message.Contains("groupe Hue a changé"); }
            check(refused && bridge.Calls.All(c => c.Method == HttpMethod.Get),
                "A cached color group widened outside the app is rejected before sending a color");
            bridge.GroupChanged = false;
            bridge.Calls.Clear();
            await output.RestoreAsync(CancellationToken.None);
            check(bridge.Calls.Count(c => c.Method == HttpMethod.Put && c.Path.StartsWith("clip/v2/resource/scene/")) == 1
                && !bridge.Calls.Any(c => c.Path.StartsWith("clip/v2/resource/light/"))
                && store.Get<Dictionary<string, JsonElement>>("pending_restore") is null,
                "One scene recall restores only the captured selected lamps together");

            output = new HueOutput(bridge.Client(), store);
            await output.CaptureAsync(settings, CancellationToken.None);
            bridge.Calls.Clear();
            bridge.HasExactGroup = false;
            await output.ApplyAsync(RaceFlag.GREEN, settings.Effects[RaceFlag.GREEN], settings, CancellationToken.None);
            var creation = bridge.Calls.Single(c => c.Method == HttpMethod.Post);
            check(creation.Path == "groups" && JsonSerializer.Deserialize<JsonElement>(creation.Body).GetProperty("lights")
                    .EnumerateArray().Select(l => l.GetString()).SequenceEqual(["1", "2"])
                && bridge.Calls.Count(c => c.Method == HttpMethod.Put) == 1
                && !bridge.Calls.Any(c => c.Path.StartsWith("groups/0") || c.Path.StartsWith("groups/8")),
                "Missing color group creates an exact selection and never commands all lamps or a wider room");

            bridge.Calls.Clear();
            bridge.RejectColor = true;
            refused = false;
            try
            {
                await output.ApplyAsync(RaceFlag.RED, settings.Effects[RaceFlag.RED], settings, CancellationToken.None);
            }
            catch (InvalidOperationException e) { refused = e.Message.Contains("refusée") && !e.Message.Contains("private-test-key"); }
            check(refused && store.Get<Dictionary<string, JsonElement>>("pending_restore") is not null,
                "A grouped-color bridge error is surfaced with its key masked and restoration preserved");
            bridge.RejectColor = false;
            await output.RestoreAsync(CancellationToken.None);

            await output.CaptureAsync(settings, CancellationToken.None);
            bridge.Calls.Clear();
            await using (var engine = new EffectEngine(output))
            {
                var shortGreen = settings with
                {
                    Effects = new(settings.Effects)
                    {
                        [RaceFlag.GREEN] = settings.Effects[RaceFlag.GREEN] with
                        {
                            DurationSeconds = .1
                        }
                    }
                };
                await engine.PlayAsync(RaceFlag.GREEN, shortGreen);
                await engine.Completion.WaitAsync(TimeSpan.FromSeconds(2));
                check(engine.Active is null && bridge.Calls.Count(c => c.Method == HttpMethod.Put && c.Path == "groups/7/action") == 1
                    && bridge.Calls.Count(c => c.Method == HttpMethod.Put && c.Path.StartsWith("clip/v2/resource/scene/")) == 1
                    && store.Get<Dictionary<string, JsonElement>>("pending_restore") is null,
                    "A fixed green duration follows one grouped color command with restoration of the chosen baseline");
            }

            output = new HueOutput(bridge.Client(), store);
            bridge.OmitLegacy = true;
            var single = settings with
            {
                LightIds = [A]
            };
            await output.CaptureAsync(single, CancellationToken.None);
            bridge.Calls.Clear();
            await output.ApplyAsync(RaceFlag.GREEN, single.Effects[RaceFlag.GREEN], single, CancellationToken.None);
            check(bridge.Calls.Count == 1 && bridge.Calls.Single().Path == "clip/v2/resource/light/" + A,
                "One chosen lamp retains a single v2 color command without requiring a legacy group");
            await output.RestoreAsync(CancellationToken.None);
        }
        finally { Directory.Delete(directory, true); }
        await DarkStartAsync(check);
    }
    private static async Task DarkStartAsync(Action<bool, string> check)
    {
        foreach (var selection in new[] { "single", "all-off", "mixed" })
            foreach (var flag in new[] { RaceFlag.GREEN, RaceFlag.YELLOW, RaceFlag.RED, RaceFlag.SC_ENDING, RaceFlag.VSC_ENDING, RaceFlag.SC })
            {
                var directory = Path.Combine(Path.GetTempPath(), "f1hue-wake-" + Guid.NewGuid());
                try
                {
                    using var store = new Store(directory);
                    var clock = new FastClock();
                    var bridge = new Bridge();
                    bridge.Off.Add(A);
                    if (selection == "all-off")
                        bridge.Off.Add(B);
                    using var client = bridge.Client(clock);
                    var output = new HueOutput(client, store, clock);
                    var settings = new AppSettings { LightIds = selection == "single" ? [A] : [A, B], Brightness = 180, TransitionSeconds = .7 };
                    await output.CaptureAsync(settings, CancellationToken.None);
                    bridge.Calls.Clear();
                    await output.ApplyAsync(flag, settings.Effects[flag], settings, CancellationToken.None);
                    var write = bridge.Calls.Single(c => c.Method == HttpMethod.Put);
                    var body = JsonSerializer.Deserialize<JsonElement>(write.Body);
                    var native = flag == RaceFlag.SC || selection != "single";
                    var xy = native ? body.GetProperty("xy") : body.GetProperty("color").GetProperty("xy");
                    var transition = native ? body.GetProperty("transitiontime").GetInt32() : body.GetProperty("dynamics").GetProperty("duration").GetInt32();
                    var on = native ? body.GetProperty("on").GetBoolean() : body.GetProperty("on").GetProperty("on").GetBoolean();
                    var x = native ? xy[0].GetDouble() : xy.GetProperty("x").GetDouble();
                    check(on && transition == 0 && x == settings.Effects[flag].X
                        && (selection == "single" || write.Path == "groups/7/action")
                        && !store.Get<Dictionary<string, JsonElement>>("pending_restore")![A].GetProperty("on").GetProperty("on").GetBoolean(),
                        $"{selection}: {flag} wakes directly in its color with one command and preserves the original off state");
                    await output.EndAnimationAsync(CancellationToken.None);
                    bridge.Calls.Clear();
                    await output.ApplyAsync(RaceFlag.GREEN, settings.Effects[RaceFlag.GREEN], settings, CancellationToken.None);
                    body = JsonSerializer.Deserialize<JsonElement>(bridge.Calls.Single(c => c.Method == HttpMethod.Put).Body);
                    transition = selection == "single" ? body.GetProperty("dynamics").GetProperty("duration").GetInt32() : body.GetProperty("transitiontime").GetInt32();
                    check(transition == (selection == "single" ? 700 : 7), $"{selection}: the configured fade returns after {flag} successfully wakes the lamps");
                    await output.RestoreAsync(CancellationToken.None);
                    bridge.Calls.Clear();
                    await output.ApplyAsync(RaceFlag.GREEN, settings.Effects[RaceFlag.GREEN], settings, CancellationToken.None);
                    body = JsonSerializer.Deserialize<JsonElement>(bridge.Calls.Single(c => c.Method == HttpMethod.Put).Body);
                    transition = selection == "single" ? body.GetProperty("dynamics").GetProperty("duration").GetInt32() : body.GetProperty("transitiontime").GetInt32();
                    check(transition == 0, $"{selection}: restoring the off baseline rearms direct wake-up for the next test");
                    await output.RestoreAsync(CancellationToken.None);
                    await output.ReleaseAsync(CancellationToken.None);
                }
                finally { Directory.Delete(directory, true); }
            }
    }
    private sealed class Bridge
    {
        public readonly List<(HttpMethod Method, string Path, string Body)> Calls = [];
        public bool HasExactGroup = true, GroupChanged, RejectColor, OmitLegacy;
        public readonly HashSet<string> Off = [];
        private readonly SceneBridge Scenes = new();
        public HueClient Client(TimeProvider? time = null) => new(new MemoryVault(), (_, pin, _) =>
        {
            if (pin != "test-pin")
                throw new Exception("Grouped colors must retain certificate pinning");
            return new HttpClient(new Handler(Reply)) { BaseAddress = new Uri("https://192.168.1.20/") };
        }, time);
        private async Task<HttpResponseMessage> Reply(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/').Replace("api/private-test-key/", "", StringComparison.Ordinal);
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method, path, body));
            string response;
            if (path.StartsWith("clip/"))
            {
                var lights = new[] { Light(A, "1"), Light(B, "2"), Light("33333333-3333-3333-3333-333333333333", "3") };
                response = Scenes.Reply(path, request.Method, body, lights);
            }
            else if (path == "groups" && request.Method == HttpMethod.Get)
                response = HasExactGroup ? "{\"0\":{\"lights\":[\"1\",\"2\"]},\"8\":{\"lights\":[\"1\",\"2\",\"3\"]},\"7\":{\"lights\":[\"1\",\"2\"]}}"
                    : "{\"0\":{\"lights\":[\"1\",\"2\"]},\"8\":{\"lights\":[\"1\",\"2\",\"3\"]}}";
            else if (path == "groups" && request.Method == HttpMethod.Post)
            {
                HasExactGroup = true;
                response = "[{\"success\":{\"id\":\"7\"}}]";
            }
            else if (path == "groups/7")
                response = GroupChanged ? "{\"lights\":[\"1\",\"2\",\"3\"]}" : "{\"lights\":[\"1\",\"2\"]}";
            else if (RejectColor && path == "groups/7/action")
                response = "[{\"error\":{\"type\":7,\"description\":\"rejected /api/private-test-key/\"}}]";
            else
                response = "[{\"success\":{\"state\":true}}]";
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(response, System.Text.Encoding.UTF8, "application/json")
            };
        }
        private object Light(string id, string legacy) => new
        {
            id,
            type = "light",
            owner = new
            {
                rid = SceneBridge.Device(id),
                rtype = "device"
            },
            id_v1 = OmitLegacy ? null : "/lights/" + legacy,
            on = new
            {
                on = !Off.Contains(id)
            },
            dimming = new
            {
                brightness = 42
            },
            color = new
            {
                xy = new
                {
                    x = .3,
                    y = .4
                }
            }
        };
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
    private sealed class FastClock : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            // These tests advance short settling/budget delays only. Native
            // renewal is covered separately and stays paused until Stop here.
            => System.CreateTimer(callback, state, dueTime >= TimeSpan.FromSeconds(2) ? Timeout.InfiniteTimeSpan : dueTime / 100,
                period == Timeout.InfiniteTimeSpan ? period : period / 100);
    }
}
