using System.Text.Json;

namespace F1Hue.Infrastructure;

internal sealed record HueGroupLease(string[] Lights, string? Group);
internal sealed record HueOwnedGroup(string Name, string[] Lights, string? Group = null);

// One bridge command addresses the whole selection. Never use group 0 or a
// room containing other lamps, and verify membership before every group write.
internal sealed class HueGroupTarget(HueClient client, string[] lights, string? group)
{
    public HueGroupLease Lease => new(lights.ToArray(), group);
    public static HueGroupTarget FromLease(HueClient client, HueGroupLease lease)
    {
        foreach (var light in lease.Lights)
            LightId("/lights/" + light);
        if (lease.Lights.Length == 0 || lease.Lights.Distinct().Count() != lease.Lights.Length
            || lease.Group is null && lease.Lights.Length != 1
            || lease.Group is not null && !System.Text.RegularExpressions.Regex.IsMatch(lease.Group, @"\A[1-9][0-9]*\z"))
            throw new InvalidOperationException("Cible Hue sauvegardée invalide.");
        return new(client, lease.Lights.ToArray(), lease.Group);
    }
    public static string LightId(string? legacyPath)
        => legacyPath is not null && System.Text.RegularExpressions.Regex.IsMatch(legacyPath, @"\A/lights/[1-9][0-9]*\z")
            ? legacyPath[8..] : throw new InvalidOperationException("Cette lampe ne prend pas en charge la synchronisation native Hue.");

    private static bool Matches(JsonElement state, string[] selected)
        => state.TryGetProperty("lights", out var members) && members.ValueKind == JsonValueKind.Array
            && members.EnumerateArray().Select(l => l.GetString()).ToHashSet().SetEquals(selected);

    public static async Task<HueGroupTarget?> FindExistingAsync(HueClient client, string[] lights, CancellationToken ct)
    {
        foreach (var light in lights)
            LightId("/lights/" + light);
        if (lights.Length <= 1)
            return null;
        var groups = await client.LegacyRequestAsync(HttpMethod.Get, "groups", null, ct);
        if (groups.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Liste des groupes Hue invalide.");
        var group = groups.EnumerateObject().Where(g => System.Text.RegularExpressions.Regex.IsMatch(g.Name, @"\A[1-9][0-9]*\z")
            && Matches(g.Value, lights)).Select(g => g.Name).FirstOrDefault();
        return group is null ? null : new(client, lights.ToArray(), group);
    }

    public static async Task<HueGroupTarget> ResolveAsync(HueClient client, string[] lights, CancellationToken ct, Store? store = null)
    {
        if (lights.Length == 0 || lights.Distinct().Count() != lights.Length)
            throw new InvalidOperationException("Sélection Hue native invalide.");
        foreach (var light in lights)
            LightId("/lights/" + light);
        string? group = null;
        if (lights.Length > 1)
        {
            group = (await FindExistingAsync(client, lights, ct))?.Lease.Group;
            if (group is null)
            {
                // Reuse an exact group or create one; never widen an existing group.
                if (store is null)
                    throw new InvalidOperationException("Le groupe Hue ne peut pas être créé sans sauvegarde de récupération.");
                if (store.Get<HueOwnedGroup>("hue_owned_group") is not null)
                    await ReleaseOwnedAsync(client, store, ct);
                var owned = new HueOwnedGroup("F1Hue-" + Guid.NewGuid().ToString("N")[..16], lights.ToArray());
                store.Put("hue_owned_group", owned);
                var created = await client.LegacyRequestAsync(HttpMethod.Post, "groups", new
                {
                    name = owned.Name,
                    type = "LightGroup",
                    lights
                }, ct);
                group = created[0].GetProperty("success").GetProperty("id").GetString();
                if (group is null || !System.Text.RegularExpressions.Regex.IsMatch(group, @"\A[1-9][0-9]*\z"))
                    throw new InvalidOperationException("Le pont n’a pas créé le groupe de synchronisation.");
                store.Put("hue_owned_group", owned with
                {
                    Group = group
                });
            }
        }
        return new(client, lights.ToArray(), group);
    }

    public async Task WriteAsync(object body, CancellationToken ct, bool urgent = false)
    {
        using var budget = group is not null ? await client.GroupBudgetAsync(urgent, ct) : null;
        if (group is not null)
        {
            var state = await client.LegacyRequestAsync(HttpMethod.Get, "groups/" + group, null, ct);
            if (!Matches(state, lights))
                throw new InvalidOperationException("Le groupe Hue a changé. Arrête le mode puis sélectionne à nouveau les lampes.");
        }
        await client.LegacyRequestAsync(HttpMethod.Put, group is not null ? "groups/" + group + "/action" : "lights/" + lights.Single() + "/state", body, ct);
    }
    public static async Task ReleaseOwnedAsync(HueClient client, Store store, CancellationToken ct)
    {
        var owned = store.Get<HueOwnedGroup>("hue_owned_group");
        if (owned is null)
            return;
        var groups = await client.LegacyRequestAsync(HttpMethod.Get, "groups", null, ct);
        foreach (var item in groups.EnumerateObject())
        {
            if (owned.Group is not null && item.Name != owned.Group)
                continue;
            if (!item.Value.TryGetProperty("name", out var name) || name.GetString() != owned.Name
                || !item.Value.TryGetProperty("type", out var type) || type.GetString() != "LightGroup" || !Matches(item.Value, owned.Lights))
                continue;
            await client.LegacyRequestAsync(HttpMethod.Delete, "groups/" + item.Name, null, ct);
        }
        store.Delete("hue_owned_group");
    }
}
