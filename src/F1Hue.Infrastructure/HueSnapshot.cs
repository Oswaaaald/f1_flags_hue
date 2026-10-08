using System.Security.Cryptography;
using System.Text.Json;
using F1Hue.Core;

namespace F1Hue.Infrastructure;

internal sealed record HueSnapshotLease(string Tag, string[] Lights, Dictionary<string, JsonElement> States, string? Zone = null, string? Scene = null);

// Scene actions preserve each lamp's own state, and one recall applies them
// together. Only resources created with this persisted lease may be removed.
internal sealed class HueSnapshot(HueClient client, Store store, TimeProvider? time = null)
{
    public const string Key = "hue_snapshot";
    private static JsonElement[] Items(JsonElement data) => data.ValueKind == JsonValueKind.Array
        ? data.EnumerateArray().ToArray() : throw new InvalidOperationException("Réponse Hue invalide.");
    private static string Id(JsonElement resource) => resource.GetProperty("id").GetString()!;
    private static string Name(HueSnapshotLease lease) => "F1 Hue Sync " + lease.Tag;
    private const string SceneName = "F1 Hue Sync · état initial";
    private static bool SameSet(IEnumerable<string?> actual, IEnumerable<string> wanted) => actual.ToHashSet().SetEquals(wanted);

    public async Task PrepareAsync(Dictionary<string, JsonElement> baseline, CancellationToken ct, JsonElement? knownResources = null)
    {
        if (store.Get<HueSnapshotLease>(Key) is not null)
            throw new InvalidOperationException("Termine le nettoyage Hue avant de lancer un effet.");
        var resources = Items(knownResources ?? await client.RequestAsync(HttpMethod.Get, "", null, ct));
        var ids = baseline.Keys.ToArray();
        var selected = resources.Where(r => r.GetProperty("type").GetString() == "light" && baseline.ContainsKey(Id(r))).ToArray();
        if (selected.Length != ids.Length || selected.Any(r => !r.TryGetProperty("owner", out _)))
            throw new InvalidOperationException("Les lampes Hue ne peuvent pas être sauvegardées ensemble. Actualise la sélection.");
        var devices = selected.Select(r => r.GetProperty("owner").GetProperty("rid").GetString()!).Distinct().ToArray();
        var disconnected = resources.Where(r => r.GetProperty("type").GetString() == "zigbee_connectivity"
            && devices.Contains(r.GetProperty("owner").GetProperty("rid").GetString())
            && r.GetProperty("status").GetString() != "connected").Any();
        if (disconnected)
            throw new InvalidOperationException("Une lampe sélectionnée est hors ligne. Reconnecte-la avant de lancer un effet.");
        var lease = new HueSnapshotLease(Convert.ToHexString(RandomNumberGenerator.GetBytes(8)), ids, baseline);
        store.Put(Key, lease); // A cancelled creation may have reached the bridge.
        var zone = Items(await client.RequestAsync(HttpMethod.Post, "/zone", new
        {
            metadata = new
            {
                name = Name(lease),
                archetype = "other"
            },
            // Unlike rooms (device children), v2 zones contain light services.
            children = ids.Select(id => new { rid = id, rtype = "light" }),
        }, ct)).Single();
        var zoneId = zone.GetProperty("rid").GetString()!;
        if (!Guid.TryParse(zoneId, out _))
            throw new InvalidOperationException("Zone de restauration Hue invalide.");
        lease = lease with
        {
            Zone = zoneId
        };
        store.Put(Key, lease);
        var created = Items(await client.RequestAsync(HttpMethod.Post, "/scene", new
        {
            metadata = new
            {
                name = SceneName,
                appdata = lease.Tag
            },
            group = new
            {
                rid = lease.Zone,
                rtype = "zone"
            },
            auto_dynamic = false,
            actions = baseline.Select(l => new { target = new { rid = l.Key, rtype = "light" }, action = l.Value }),
        }, ct)).Single();
        var sceneId = created.GetProperty("rid").GetString()!;
        if (!Guid.TryParse(sceneId, out _))
            throw new InvalidOperationException("Scène de restauration Hue invalide.");
        lease = lease with
        {
            Scene = sceneId
        };
        store.Put(Key, lease);
        await ValidateAsync(lease, baseline, ct);
    }

    private async Task ValidateAsync(HueSnapshotLease lease, Dictionary<string, JsonElement> baseline, CancellationToken ct)
    {
        var responses = await Task.WhenAll(client.RequestAsync(HttpMethod.Get, "/zone/" + lease.Zone, null, ct),
            client.RequestAsync(HttpMethod.Get, "/scene/" + lease.Scene, null, ct));
        var zones = Items(responses[0]);
        if (zones.Length == 0 || Items(responses[1]).Length == 0)
            throw new HueResourceMissingException();
        var zone = zones.Single();
        if (zone.GetProperty("metadata").GetProperty("name").GetString() != Name(lease)
            || zone.GetProperty("children").EnumerateArray().Any(c => c.GetProperty("rtype").GetString() != "light")
            || !SameSet(zone.GetProperty("children").EnumerateArray()
                .Select(c => c.GetProperty("rid").GetString()), lease.Lights))
            throw new InvalidOperationException("La zone de restauration Hue a été modifiée. Arrête le mode.");
        var scene = Items(responses[1]).Single();
        if (!OwnedScene(scene, lease, baseline))
            throw new InvalidOperationException("La scène de restauration Hue a été modifiée. Aucun rappel élargi n’a été envoyé.");
    }

    private static bool OwnedScene(JsonElement scene, HueSnapshotLease lease, Dictionary<string, JsonElement> baseline)
    {
        var actions = scene.GetProperty("actions").EnumerateArray().ToArray();
        return scene.GetProperty("metadata").TryGetProperty("appdata", out var tag) && tag.GetString() == lease.Tag
            && scene.GetProperty("metadata").GetProperty("name").GetString() == SceneName
            && scene.GetProperty("group").GetProperty("rid").GetString() == lease.Zone
            && scene.GetProperty("group").GetProperty("rtype").GetString() == "zone"
            && !(scene.TryGetProperty("auto_dynamic", out var dynamic) && dynamic.GetBoolean())
            && actions.Length == baseline.Count
            && SameSet(actions.Select(a => a.GetProperty("target").GetProperty("rid").GetString()), baseline.Keys)
            && actions.All(a => a.GetProperty("target").GetProperty("rtype").GetString() == "light"
                && ContainsState(a.GetProperty("action"), baseline[a.GetProperty("target").GetProperty("rid").GetString()!]));
    }

    private static bool ContainsState(JsonElement actual, JsonElement expected)
    {
        if (actual.ValueKind != expected.ValueKind)
            return false;
        return expected.ValueKind switch
        {
            JsonValueKind.Object => expected.EnumerateObject().All(p => actual.TryGetProperty(p.Name, out var value) && ContainsState(value, p.Value)),
            JsonValueKind.Array => actual.GetArrayLength() == expected.GetArrayLength()
                && actual.EnumerateArray().Zip(expected.EnumerateArray()).All(p => ContainsState(p.First, p.Second)),
            JsonValueKind.Number => Math.Abs(actual.GetDouble() - expected.GetDouble()) <= .0001,
            _ => actual.GetRawText() == expected.GetRawText(),
        };
    }

    public async Task RestoreAsync(Dictionary<string, JsonElement> baseline, CancellationToken ct)
    {
        var lease = store.Get<HueSnapshotLease>(Key) ?? throw new InvalidOperationException("Scène de restauration Hue manquante.");
        if (lease.Scene is null || lease.Zone is null)
            throw new InvalidOperationException("La préparation de la restauration Hue est incomplète.");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await ValidateAsync(lease, baseline, ct);
            await client.RequestAsync(HttpMethod.Put, "/scene/" + lease.Scene, new
            {
                recall = new
                {
                    action = "active",
                    duration = 400
                }
            }, ct);
            // A successful HTTP response queues the Zigbee recall. Keep both
            // the scene and its group alive through the fade and any last pulse.
            await Task.Delay(TimeSpan.FromMilliseconds(650), time ?? TimeProvider.System, ct);
            if (!await ConfirmedAsync(baseline, ct))
                continue;
            await Task.Delay(TimeSpan.FromMilliseconds(350), time ?? TimeProvider.System, ct);
            if (await ConfirmedAsync(baseline, ct))
                return;
        }
        throw new InvalidOperationException("La restauration d’une lampe n’est pas confirmée par le pont. L’état initial est conservé : réessaie Stop.");
    }

    public async Task<bool> ConfirmedAsync(Dictionary<string, JsonElement> baseline, CancellationToken ct)
    {
        var lights = Items(await client.RequestAsync(HttpMethod.Get, "/light", null, ct)).ToDictionary(Id);
        return baseline.All(item => lights.TryGetValue(item.Key, out var light) && RestoredState(light, item.Value));
    }
    private static bool RestoredState(JsonElement light, JsonElement wanted)
    {
        var actual = HueOutput.Baseline(light);
        if (!actual.GetProperty("on").GetProperty("on").GetBoolean())
            return !wanted.GetProperty("on").GetProperty("on").GetBoolean();
        return SameState(actual, wanted);
    }
    private static bool SameState(JsonElement actual, JsonElement wanted, string property = "")
    {
        if (actual.ValueKind != wanted.ValueKind)
            return false;
        return wanted.ValueKind switch
        {
            JsonValueKind.Object => wanted.EnumerateObject().All(p => actual.TryGetProperty(p.Name, out var value) && SameState(value, p.Value, p.Name)),
            JsonValueKind.Array => actual.GetArrayLength() == wanted.GetArrayLength()
                && actual.EnumerateArray().Zip(wanted.EnumerateArray()).All(p => SameState(p.First, p.Second)),
            // Brightness and xy are quantized by the lamp/bridge. Tolerances
            // cover that quantization, not a visibly different flag color.
            JsonValueKind.Number => Math.Abs(actual.GetDouble() - wanted.GetDouble()) <= (property == "brightness" ? 1 : property == "mirek" ? 1 : .002),
            _ => actual.GetRawText() == wanted.GetRawText(),
        };
    }

    public async Task ReleaseAsync(CancellationToken ct)
    {
        if (store.Get<HueSnapshotLease>(Key) is not { } lease)
            return;
        // Discover resources by the creation token as well: a response could
        // have been lost before its ID was saved. Never delete another scene.
        var resources = Items(await client.RequestAsync(HttpMethod.Get, "", null, ct));
        var scenes = resources.Where(r => r.GetProperty("type").GetString() == "scene" && OwnedScene(r, lease, lease.States)).ToArray();
        foreach (var scene in scenes)
            await client.RequestAsync(HttpMethod.Delete, "/scene/" + Id(scene), null, ct);
        var zones = resources.Where(r => r.GetProperty("type").GetString() == "zone"
            && r.GetProperty("metadata").GetProperty("name").GetString() == Name(lease)).ToArray();
        foreach (var zone in zones)
        {
            var zoneId = Id(zone);
            // A zone adopted by another scene or edited externally is retained.
            if (zone.GetProperty("children").EnumerateArray().Any(c => c.GetProperty("rtype").GetString() != "light")
                || !SameSet(zone.GetProperty("children").EnumerateArray().Select(c => c.GetProperty("rid").GetString()), lease.Lights)
                || resources.Any(r => r.GetProperty("type").GetString() == "scene"
                    && r.GetProperty("group").GetProperty("rid").GetString() == zoneId && !scenes.Any(s => Id(s) == Id(r))))
                continue;
            await client.RequestAsync(HttpMethod.Delete, "/zone/" + zoneId, null, ct);
        }
        store.Delete(Key);
    }
}
