using System.Text.Json;
using F1Hue.Core;

namespace F1Hue.Infrastructure;

public sealed class HueOutput(HueClient client, Store store, TimeProvider? time = null) : IEffectOutput, IEffectFailureSource
{
    public event Action<Exception>? Failed;
    private Dictionary<string, JsonElement> _baseline = [];
    private HueNativePulse? _pulse;
    private HueGroupTarget? _solidTarget;
    private readonly HueSnapshot _snapshot = new(client, store, time);
    private bool _restored;
    private string?[] _legacyLights = [];
    public async Task CaptureAsync(AppSettings settings, CancellationToken ct)
    {
        if (store.Get<Dictionary<string, JsonElement>>("pending_restore") is not null || store.Get<string>("pending_entertainment") is not null
            || store.Get<string[]>("pending_native_alert") is not null || store.Get<HueSnapshotLease>(HueSnapshot.Key) is not null)
            await RecoverAsync(ct);
        // A failed new capture must not reuse a previous mode's lamps, especially
        // when the user changed the selection while idle.
        _baseline = [];
        _legacyLights = [];
        _solidTarget = null;
        _restored = false;
        var lights = await client.RequestAsync(HttpMethod.Get, "/light", null, ct);
        var selected = lights.EnumerateArray().Where(l => settings.LightIds.Contains(l.GetProperty("id").GetString()!)).ToArray();
        if (selected.Length != settings.LightIds.Length || selected.Length == 0 || selected.Any(l => !l.TryGetProperty("color", out _)))
            throw new InvalidOperationException("La sélection Hue a changé. Choisis à nouveau les lampes.");
        _baseline = selected.ToDictionary(l => l.GetProperty("id").GetString()!, Baseline);
        _legacyLights = selected.Select(l => l.TryGetProperty("id_v1", out var legacy) ? legacy.GetString() : null).ToArray();
        store.Put("pending_restore", _baseline);
        if (_baseline.Count > 1) await _snapshot.PrepareAsync(_baseline, ct);
    }
    public static JsonElement Baseline(JsonElement light)
    {
        var result = new Dictionary<string, object> { ["on"] = light.GetProperty("on").Clone() };
        if (light.TryGetProperty("dimming", out var dim)) result["dimming"] = new { brightness = dim.GetProperty("brightness").GetDouble() };
        if (light.TryGetProperty("color_temperature", out var temp) && temp.TryGetProperty("mirek_valid", out var valid) && valid.ValueKind == JsonValueKind.True && temp.GetProperty("mirek").ValueKind == JsonValueKind.Number)
            result["color_temperature"] = new { mirek = temp.GetProperty("mirek").GetInt32() };
        else if (light.TryGetProperty("color", out var color)) result["color"] = new { xy = color.GetProperty("xy").Clone() };
        if (light.TryGetProperty("gradient", out var gradient) && gradient.GetProperty("points").GetArrayLength() >= 2)
        {
            var state = new Dictionary<string, object> { ["points"] = gradient.GetProperty("points").Clone() };
            if (gradient.TryGetProperty("mode", out var mode)) state["mode"] = mode.GetString()!;
            result["gradient"] = state;
        }
        if (light.TryGetProperty("effects", out var effects) && effects.TryGetProperty("status", out var status) && status.GetString() is string value)
            result["effects"] = new { effect = value };
        return RestoreState(JsonSerializer.SerializeToElement(result, JsonDefaults.Options));
    }
    private static JsonElement RestoreState(JsonElement state)
    {
        var fields = state.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        // Hue scene actions allow one color mode. In particular, even
        // effects=no_effect cannot be combined with color/temperature/gradient.
        // Sending the selected color mode stops an effect implicitly.
        if (fields.TryGetValue("effects", out var effect) && effect.GetProperty("effect").GetString() is not (null or "no_effect"))
        {
            fields.Remove("color"); fields.Remove("color_temperature"); fields.Remove("gradient");
        }
        else
        {
            fields.Remove("effects");
            if (fields.ContainsKey("gradient")) { fields.Remove("color"); fields.Remove("color_temperature"); }
        }
        return JsonSerializer.SerializeToElement(fields, JsonDefaults.Options);
    }
    public async Task ApplyAsync(RaceFlag flag, EffectSpec effect, AppSettings settings, CancellationToken ct)
    {
        store.Put("pending_restore", _baseline);
        _restored = false;
        if (effect.Mode == "blink")
        {
            _pulse = new HueNativePulse(client, store, e => Failed?.Invoke(e), time);
            await _pulse.StartAsync(_legacyLights.Select(HueNativePulse.LightId).ToArray(), flag, effect, settings, ct);
            return;
        }
        if (settings.LightIds.Length > 1)
        {
            _solidTarget ??= await HueGroupTarget.ResolveAsync(client, _legacyLights.Select(HueGroupTarget.LightId).ToArray(), ct);
            await _solidTarget.WriteAsync(new { on = true, bri = settings.Brightness, xy = new[] { effect.X, effect.Y },
                transitiontime = (int)Math.Round(settings.TransitionSeconds * 10), effect = "none", alert = "none" }, ct);
            return;
        }
        var state = new Dictionary<string, object>
        {
            ["on"] = new { on = true }, ["dimming"] = new { brightness = settings.Brightness / 254.0 * 100 },
            ["color"] = new { xy = new { x = effect.X, y = effect.Y } }, ["dynamics"] = new { duration = (int)(settings.TransitionSeconds * 1000) },
        };
        await client.PutLightAsync(settings.LightIds.Single(), state, ct);
    }
    public async Task EndAnimationAsync(CancellationToken ct)
    {
        if (_pulse is not null || store.Get<string[]>("pending_native_alert") is not null)
        {
            _pulse ??= new HueNativePulse(client, store);
            await _pulse.StopAsync(ct); _pulse = null;
            _restored = false;
        }
        if (store.Get<string[]>("pending_native_alert") is null) store.Delete("pending_native_group");
        if (store.Get<string>("pending_entertainment") is string area)
        {
            await client.RequestAsync(HttpMethod.Put, "/entertainment_configuration/" + area, new { action = "stop" }, ct);
            store.Delete("pending_entertainment");
        }
    }
    public async Task RestoreAsync(CancellationToken ct)
    {
        if (!_restored && _baseline.Count > 1)
        {
            if (store.Get<HueSnapshotLease>(HueSnapshot.Key) is not { Scene: not null, Zone: not null })
            {
                // Upgrade a persisted baseline from an older app, or retry a
                // capture whose scene creation failed before any effect played.
                await _snapshot.ReleaseAsync(ct);
                await _snapshot.PrepareAsync(_baseline, ct);
            }
            await _snapshot.RestoreAsync(_baseline, ct);
            _restored = true;
        }
        List<Exception> errors = [];
        if (!_restored)
            foreach (var (id, state) in _baseline)
                try { await client.PutLightAsync(id, state, ct); }
                catch (Exception e) { errors.Add(e); }
        if (errors.Count > 0) throw new InvalidOperationException("Une lampe n’a pas pu être restaurée : " + errors[0].Message);
        _restored = true;
        if (_pulse is null && store.Get<string[]>("pending_native_alert") is null && store.Get<string>("pending_entertainment") is null)
            store.Delete("pending_restore");
    }
    public async Task RecoverAsync(CancellationToken ct)
    {
        _baseline = (store.Get<Dictionary<string, JsonElement>>("pending_restore") ?? []).ToDictionary(p => p.Key, p => RestoreState(p.Value));
        _restored = false;
        if (_baseline.Count == 0 && store.Get<string>("pending_entertainment") is null && store.Get<string[]>("pending_native_alert") is null)
        { await _snapshot.ReleaseAsync(ct); return; }
        await EndAnimationAsync(ct); await RestoreAsync(ct); await _snapshot.ReleaseAsync(ct);
    }
    public Task ForgetAsync(CancellationToken ct) { store.Delete("pending_restore"); return Task.CompletedTask; }
    public Task ReleaseAsync(CancellationToken ct) => _snapshot.ReleaseAsync(ct);
}
