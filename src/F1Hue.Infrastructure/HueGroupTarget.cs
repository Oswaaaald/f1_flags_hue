using System.Text.Json;

namespace F1Hue.Infrastructure;

internal sealed record HueGroupLease(string[] Lights, string? Group);

// One bridge command addresses the whole selection. Never use group 0 or a
// room containing other lamps, and verify membership before every group write.
internal sealed class HueGroupTarget(HueClient client, string[] lights, string? group)
{
    public HueGroupLease Lease => new(lights.ToArray(), group);
    public static HueGroupTarget FromLease(HueClient client, HueGroupLease lease)
    {
        foreach (var light in lease.Lights) LightId("/lights/" + light);
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

    public static async Task<HueGroupTarget> ResolveAsync(HueClient client, string[] lights, CancellationToken ct)
    {
        if (lights.Length == 0 || lights.Distinct().Count() != lights.Length)
            throw new InvalidOperationException("Sélection Hue native invalide.");
        foreach (var light in lights) LightId("/lights/" + light);
        string? group = null;
        if (lights.Length > 1)
        {
            var groups = await client.LegacyRequestAsync(HttpMethod.Get, "groups", null, ct);
            if (groups.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Liste des groupes Hue invalide.");
            group = groups.EnumerateObject().Where(g => System.Text.RegularExpressions.Regex.IsMatch(g.Name, @"\A[1-9][0-9]*\z")
                && Matches(g.Value, lights)).Select(g => g.Name).FirstOrDefault();
            if (group is null)
            {
                // Reuse an exact group or create one; never widen an existing group.
                var created = await client.LegacyRequestAsync(HttpMethod.Post, "groups", new { name = "F1 Hue Sync", type = "LightGroup", lights }, ct);
                group = created[0].GetProperty("success").GetProperty("id").GetString();
                if (group is null || !System.Text.RegularExpressions.Regex.IsMatch(group, @"\A[1-9][0-9]*\z"))
                    throw new InvalidOperationException("Le pont n’a pas créé le groupe de synchronisation.");
            }
        }
        return new(client, lights.ToArray(), group);
    }

    public async Task WriteAsync(object body, CancellationToken ct)
    {
        if (group is not null)
        {
            var state = await client.LegacyRequestAsync(HttpMethod.Get, "groups/" + group, null, ct);
            if (!Matches(state, lights)) throw new InvalidOperationException("Le groupe Hue a changé. Arrête le mode puis sélectionne à nouveau les lampes.");
        }
        await client.LegacyRequestAsync(HttpMethod.Put, group is not null ? "groups/" + group + "/action" : "lights/" + lights.Single() + "/state", body, ct);
    }
}
