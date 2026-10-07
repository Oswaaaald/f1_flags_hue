using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using F1Hue.Core;
using F1Hue.Infrastructure;

internal static class SnapshotTests
{
    private const string A = "11111111-1111-1111-1111-111111111111", B = "22222222-2222-2222-2222-222222222222";
    private static JsonElement J(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    public static async Task Run(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "f1hue-snapshots-" + Guid.NewGuid());
        try
        {
            using var store = new Store(directory);
            var bridge = new Bridge();
            using var client = bridge.Client();
            var output = new HueOutput(client, store);
            var settings = new AppSettings { LightIds = [A, B] };
            await output.CaptureAsync(settings, CancellationToken.None);
            var inventory = await client.InventoryAsync(CancellationToken.None);
            check(inventory.Groups.Single(g => g.Type == "zone").LightIds.ToHashSet().SetEquals([A, B]),
                "Hue v2 zones use light children and inventory resolves their exact lamps");
            var scene = bridge.Scene;
            var actions = scene["actions"]!.AsArray();
            var first = actions.Single(a => a!["target"]!["rid"]!.GetValue<string>() == A)!["action"]!;
            var second = actions.Single(a => a!["target"]!["rid"]!.GetValue<string>() == B)!["action"]!;
            check(actions.Count == 2 && !first["on"]!["on"]!.GetValue<bool>() && first["dimming"]!["brightness"]!.GetValue<double>() == 21
                && second["on"]!["on"]!.GetValue<bool>() && second["dimming"]!["brightness"]!.GetValue<double>() == 78
                && first["color_temperature"]!["mirek"]!.GetValue<int>() == 250 && second["gradient"]!["points"]![0]!["color"]!["xy"]!["x"]!.GetValue<double>() == .1,
                "The restore scene keeps distinct brightness, on/off, white temperature and color for exactly the selection");
            check(second["gradient"]!["mode"]!.GetValue<string>() == "interpolated_palette"
                && second["gradient"]!["points"]!.AsArray().Count == 2 && second["effects"] is null && second["color"] is null,
                "Gradient points and mode survive capture without incompatible effect or solid-color fields");
            var native = HueOutput.Baseline(J("{\"on\":{\"on\":true},\"color\":{\"xy\":{\"x\":0.2,\"y\":0.3}},\"effects\":{\"status\":\"candle\"}}"));
            check(native.GetProperty("effects").GetProperty("effect").GetString() == "candle" && !native.TryGetProperty("color", out _),
                "An active native effect is restored as its own color mode, not mixed with xy");
            bridge.Calls.Clear(); await output.ApplyAsync(RaceFlag.GREEN, settings.Effects[RaceFlag.GREEN], settings, CancellationToken.None);
            await output.RestoreAsync(CancellationToken.None); await output.RestoreAsync(CancellationToken.None);
            check(bridge.Calls.Count(c => c.Method == HttpMethod.Put && c.Path.StartsWith("clip/v2/resource/scene/")) == 1
                && !bridge.Calls.Any(c => c.Path.StartsWith("clip/v2/resource/light/")),
                "A baseline is recalled once for all lamps, including repeated cleanup after a fixed effect expires");
            await output.ReleaseAsync(CancellationToken.None);
            check(bridge.Scenes.Resources.Count == 0 && store.Get<JsonElement?>("hue_snapshot") is null,
                "Stop deletes only its temporary scene and zone and leaves no repeated-test resource accumulation");

            await output.CaptureAsync(settings, CancellationToken.None); bridge.Calls.Clear();
            bridge.Scenes.MissNextRecall = true;
            await output.RestoreAsync(CancellationToken.None);
            var recalls = bridge.Calls.Where(c => c.Method == HttpMethod.Put && c.Path.StartsWith("clip/v2/resource/scene/")).ToArray();
            check(recalls.Length == 2 && recalls.Select(c => c.Path).Distinct().Count() == 1
                && !bridge.Calls.Any(c => c.Method == HttpMethod.Put && c.Path.StartsWith("clip/v2/resource/light/")),
                "A successful recall that leaves one lamp orange is detected and retried with the same synchronized scene");
            check(bridge.Calls.Last().Method == HttpMethod.Get && bridge.Calls.Last().Path == "clip/v2/resource/light"
                && bridge.Scenes.Resources.Count == 2 && store.Get<JsonElement?>("pending_restore") is null,
                "Scene and zone remain alive until two settled light-state reads confirm restoration");
            await output.ReleaseAsync(CancellationToken.None);

            await output.CaptureAsync(settings, CancellationToken.None); bridge.Scenes.MissAllRecalls = true;
            var rejected = false;
            try { await output.RestoreAsync(CancellationToken.None); } catch (InvalidOperationException e) { rejected = e.Message.Contains("pas confirmée"); }
            check(rejected && store.Get<JsonElement?>("pending_restore") is not null && bridge.Scenes.Resources.Count == 2,
                "An unconfirmed lamp never reports Stop success or loses its original state and scene");
            bridge.Scenes.MissAllRecalls = false;
            await new HueOutput(client, store).RecoverAsync(CancellationToken.None);
            check(bridge.Scenes.Resources.Count == 0 && store.Get<JsonElement?>("pending_restore") is null,
                "Retry after a partially applied recall confirms every selected lamp before releasing the recovery resources");

            await output.CaptureAsync(settings, CancellationToken.None);
            bridge.Scenes.RejectRecall = true;
            rejected = false;
            try { await output.RestoreAsync(CancellationToken.None); } catch (InvalidOperationException) { rejected = true; }
            check(rejected && store.Get<JsonElement?>("pending_restore") is not null && store.Get<JsonElement?>("hue_snapshot") is not null,
                "A failed scene recall preserves the original baseline and recovery lease");
            bridge.Scenes.RejectRecall = false; bridge.Calls.Clear();
            await new HueOutput(client, store).RecoverAsync(CancellationToken.None);
            check(bridge.Calls.Count(c => c.Method == HttpMethod.Put && c.Path.StartsWith("clip/v2/resource/scene/")) == 1
                && bridge.Scenes.Resources.Count == 0 && store.Get<JsonElement?>("pending_restore") is null,
                "Restart recovers all selected states together and then cleans up a failed recall");

            await output.CaptureAsync(settings, CancellationToken.None);
            var zone = bridge.Scenes.Resources.Values.Single(r => r["type"]!.GetValue<string>() == "zone");
            zone["children"]!.AsArray().Add(JsonSerializer.SerializeToNode(new { rid = Bridge.Outside, rtype = "light" }));
            bridge.Calls.Clear(); rejected = false;
            try { await output.RestoreAsync(CancellationToken.None); } catch (InvalidOperationException) { rejected = true; }
            check(rejected && bridge.Calls.All(c => c.Method == HttpMethod.Get) && store.Get<JsonElement?>("pending_restore") is not null,
                "An externally widened restore zone is rejected before any scene recall can affect another lamp");
            zone["children"]!.AsArray().RemoveAt(2); await output.RestoreAsync(CancellationToken.None); await output.ReleaseAsync(CancellationToken.None);

            await output.CaptureAsync(settings, CancellationToken.None);
            scene = bridge.Scene; scene["actions"]![0]!["action"]!["dimming"]!["brightness"] = 99;
            bridge.Calls.Clear(); rejected = false;
            try { await output.RestoreAsync(CancellationToken.None); } catch (InvalidOperationException) { rejected = true; }
            check(rejected && bridge.Calls.All(c => c.Method == HttpMethod.Get), "An edited scene state is not recalled as the original baseline");
            scene["actions"]![0]!["action"]!["dimming"]!["brightness"] = 21;
            await output.RestoreAsync(CancellationToken.None);
            scene["metadata"]!["name"] = "My retained scene";
            await output.ReleaseAsync(CancellationToken.None);
            check(bridge.Scenes.Resources.Count == 2, "A scene adopted or edited externally and its zone are retained during cleanup");
            bridge.Scenes.Resources.Clear();

            bridge.Scenes.LoseSceneResponse = true; rejected = false;
            try { await output.CaptureAsync(settings, CancellationToken.None); } catch (InvalidOperationException) { rejected = true; }
            check(rejected && bridge.Scenes.Resources.Count == 2 && store.Get<JsonElement?>("pending_restore") is not null,
                "A scene creation whose reply is lost retains the creation token and baseline");
            await new HueOutput(client, store).RecoverAsync(CancellationToken.None);
            check(bridge.Scenes.Resources.Count == 0 && store.Get<JsonElement?>("pending_restore") is null,
                "Recovery finds resources from a lost creation reply, replaces the incomplete snapshot and restores safely");

            bridge.Scenes.Offline = true; rejected = false;
            try { await output.CaptureAsync(settings, CancellationToken.None); } catch (InvalidOperationException e) { rejected = e.Message.Contains("hors ligne"); }
            check(rejected && bridge.Scenes.Resources.Count == 0, "An offline selected lamp prevents a partial group snapshot from starting an effect");
            bridge.Scenes.Offline = false; await new HueOutput(client, store).RecoverAsync(CancellationToken.None);

            await output.CaptureAsync(settings, CancellationToken.None); bridge.Calls.Clear();
            await output.ForgetAsync(CancellationToken.None); await output.ReleaseAsync(CancellationToken.None);
            check(!bridge.Calls.Any(c => c.Method == HttpMethod.Put) && bridge.Scenes.Resources.Count == 0,
                "Disabling restore on exit still releases the temporary Hue resources without recalling the scene");
            var single = settings with { LightIds = [B] };
            await output.CaptureAsync(single, CancellationToken.None); bridge.Calls.Clear();
            await output.ApplyAsync(RaceFlag.GREEN, single.Effects[RaceFlag.GREEN], single, CancellationToken.None);
            var solid = J(bridge.Calls.Single().Body);
            await output.RestoreAsync(CancellationToken.None);
            var restored = J(bridge.Calls.Last().Body);
            check(!solid.TryGetProperty("effects", out _) && restored.TryGetProperty("gradient", out _)
                && bridge.Scenes.Resources.Count == 0, "A single-lamp flag and gradient restoration use compatible color modes without creating a zone");
            check(bridge.ClientCreations == 1, "Hue v1/v2 requests reuse one pinned HTTPS transport instead of opening a new connection for each command");
        }
        finally { Directory.Delete(directory, true); }
    }
    private sealed class Bridge
    {
        public const string Outside = "33333333-3333-3333-3333-333333333333";
        public readonly SceneBridge Scenes = new();
        public readonly List<(HttpMethod Method, string Path, string Body)> Calls = [];
        public int ClientCreations;
        public JsonObject Scene => Scenes.Resources.Values.Single(r => r["type"]!.GetValue<string>() == "scene");
        public HueClient Client() => new(new MemoryVault(), (_, pin, _) =>
        {
            if (pin != "test-pin") throw new Exception("Snapshot requests must keep certificate pinning");
            ClientCreations++;
            return new HttpClient(new Handler(Reply)) { BaseAddress = new Uri("https://192.168.1.20/") };
        });
        private async Task<HttpResponseMessage> Reply(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/').Replace("api/private-test-key/", "", StringComparison.Ordinal);
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method, path, body));
            var response = path.StartsWith("clip/") ? Scenes.Reply(path, request.Method, body, [
                J("{\"id\":\"" + A + "\",\"type\":\"light\",\"id_v1\":\"/lights/1\",\"owner\":{\"rid\":\"" + SceneBridge.Device(A) + "\",\"rtype\":\"device\"},\"on\":{\"on\":false},\"dimming\":{\"brightness\":21},\"color_temperature\":{\"mirek\":250,\"mirek_valid\":true},\"color\":{\"xy\":{\"x\":0.3,\"y\":0.4}}}"),
                J("{\"id\":\"" + B + "\",\"type\":\"light\",\"id_v1\":\"/lights/2\",\"owner\":{\"rid\":\"" + SceneBridge.Device(B) + "\",\"rtype\":\"device\"},\"on\":{\"on\":true},\"dimming\":{\"brightness\":78},\"color\":{\"xy\":{\"x\":0.17,\"y\":0.7}},\"gradient\":{\"mode\":\"interpolated_palette\",\"points\":[{\"color\":{\"xy\":{\"x\":0.1,\"y\":0.3}}},{\"color\":{\"xy\":{\"x\":0.2,\"y\":0.4}}}]},\"effects\":{\"status\":\"no_effect\"}}"),
                new { id = Outside, type = "light", id_v1 = "/lights/3", owner = new { rid = SceneBridge.Device(Outside), rtype = "device" }, on = new { on = true }, color = new { xy = new { x = .2, y = .3 } } },
            ]) : path == "groups" ? "{\"7\":{\"lights\":[\"1\",\"2\"]}}" : path == "groups/7" ? "{\"lights\":[\"1\",\"2\"]}" : "[{\"success\":{\"state\":true}}]";
            return new(HttpStatusCode.OK) { Content = new StringContent(response, System.Text.Encoding.UTF8, "application/json") };
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
}
