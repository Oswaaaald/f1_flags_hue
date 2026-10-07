using System.Text.Json;
using F1Hue.Core;

namespace F1Hue.Infrastructure;

// The bridge renders the same alert=lselect pulse as the original Python engine.
// Only its short lease is renewed; the application never renders fade frames.
public sealed class HueNativePulse(HueClient client, Store store, Action<Exception>? failed = null, TimeProvider? time = null)
{
    private CancellationTokenSource? _cancel;
    private Task _renewal = Task.CompletedTask;
    private string? _group;
    private string[] _lights = [];
    public static string LightId(string? legacyPath)
        => legacyPath is not null && System.Text.RegularExpressions.Regex.IsMatch(legacyPath, @"\A/lights/[1-9][0-9]*\z")
            ? legacyPath[8..] : throw new InvalidOperationException("Cette lampe ne prend pas en charge la pulsation native Hue.");

    private static bool Matches(JsonElement group, string[] lights)
        => group.TryGetProperty("lights", out var members) && members.ValueKind == JsonValueKind.Array
            && members.EnumerateArray().Select(l => l.GetString()).ToHashSet().SetEquals(lights);

    public async Task StartAsync(string[] lights, RaceFlag flag, EffectSpec effect, AppSettings settings, CancellationToken ct)
    {
        if (lights.Length == 0 || lights.Distinct().Count() != lights.Length)
            throw new InvalidOperationException("Sélection Hue native invalide.");
        foreach (var light in lights) LightId("/lights/" + light);
        _lights = lights.ToArray(); _group = null;
        if (lights.Length > 1)
        {
            var groups = await client.LegacyRequestAsync(HttpMethod.Get, "groups", null, ct);
            if (groups.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Liste des groupes Hue invalide.");
            _group = groups.EnumerateObject().Where(g => System.Text.RegularExpressions.Regex.IsMatch(g.Name, @"\A[1-9][0-9]*\z")
                && Matches(g.Value, lights)).Select(g => g.Name).FirstOrDefault();
            if (_group is null)
            {
                // An exact group keeps all selected lamps in phase. Existing groups
                // are never widened or edited; an exact match is reused next time.
                var created = await client.LegacyRequestAsync(HttpMethod.Post, "groups", new { name = "F1 Hue Sync", type = "LightGroup", lights }, ct);
                _group = created[0].GetProperty("success").GetProperty("id").GetString();
                if (_group is null || !System.Text.RegularExpressions.Regex.IsMatch(_group, @"\A[1-9][0-9]*\z"))
                    throw new InvalidOperationException("Le pont n’a pas créé le groupe de synchronisation.");
            }
        }
        // Save before sending: a cancelled response may still have reached the bridge.
        store.Put("pending_native_alert", _lights);
        await ValidateGroupAsync(ct);
        await WriteAsync(new { on = true, bri = settings.Brightness, xy = new[] { effect.X, effect.Y },
            transitiontime = (int)Math.Round(settings.TransitionSeconds * 10), alert = "none" }, ct);
        var alert = flag == RaceFlag.BLUE ? "select" : "lselect";
        await WriteAsync(new { alert }, ct);
        _cancel = new CancellationTokenSource();
        // lselect expires on the bridge. Renew within its 15-second lease. Blue
        // used individual select pulses every 0.7 seconds in the Python version.
        _renewal = RenewAsync(alert, flag == RaceFlag.BLUE ? TimeSpan.FromSeconds(.7) : TimeSpan.FromSeconds(10), _cancel.Token);
    }
    private Task<JsonElement> WriteAsync(object body, CancellationToken ct)
        => client.LegacyRequestAsync(HttpMethod.Put, _group is string group ? "groups/" + group + "/action" : "lights/" + _lights.Single() + "/state", body, ct);
    private async Task ValidateGroupAsync(CancellationToken ct)
    {
        if (_group is not string group) return;
        var state = await client.LegacyRequestAsync(HttpMethod.Get, "groups/" + group, null, ct);
        if (!Matches(state, _lights)) throw new InvalidOperationException("Le groupe Hue a changé. Arrête le mode puis sélectionne à nouveau les lampes.");
    }
    private async Task RenewAsync(string alert, TimeSpan interval, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(interval, time ?? TimeProvider.System, ct);
                await ValidateGroupAsync(ct);
                await WriteAsync(new { alert }, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) { failed?.Invoke(new InvalidOperationException("La pulsation Hue a été interrompue : " + e.Message)); }
    }
    public async Task StopAsync(CancellationToken ct)
    {
        _cancel?.Cancel();
        await _renewal.WaitAsync(ct); // No renewal may arrive after alert=none.
        _cancel?.Dispose(); _cancel = null;
        var pending = store.Get<string[]>("pending_native_alert") ?? [];
        List<string> remaining = [.. pending]; List<Exception> errors = [];
        foreach (var light in pending)
        {
            try
            {
                // Cancel per saved lamp, even if the room was edited or removed.
                await client.LegacyRequestAsync(HttpMethod.Put, "lights/" + light + "/state", new { alert = "none" }, ct);
                remaining.Remove(light);
                if (remaining.Count == 0) store.Delete("pending_native_alert");
                else store.Put("pending_native_alert", remaining);
            }
            catch (Exception e) { errors.Add(e); }
            if (pending.Length > 1 && !ct.IsCancellationRequested) await Task.Delay(100, ct);
        }
        if (errors.Count > 0) throw new InvalidOperationException("Impossible d’arrêter la pulsation Hue : " + errors[0].Message);
    }
}
