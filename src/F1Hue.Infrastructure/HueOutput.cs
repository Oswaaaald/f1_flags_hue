using System.Text.Json;
using F1Hue.Core;

namespace F1Hue.Infrastructure;

public sealed class HueOutput(HueClient client, Store store, TimeProvider? time = null) : IEffectOutput, IEffectFailureSource
{
    public event Action<Exception>? Failed;
    private Dictionary<string, JsonElement> _baseline = [];
    private HueNativePulse? _pulse;
    private string?[] _legacyLights = [];
    public async Task CaptureAsync(AppSettings settings, CancellationToken ct)
    {
        if (store.Get<Dictionary<string, JsonElement>>("pending_restore") is not null || store.Get<string>("pending_entertainment") is not null
            || store.Get<string[]>("pending_native_alert") is not null)
            await RecoverAsync(ct);
        // A failed new capture must not reuse a previous mode's lamps, especially
        // when the user changed the selection while idle.
        _baseline = [];
        _legacyLights = [];
        var lights = await client.RequestAsync(HttpMethod.Get, "/light", null, ct);
        var selected = lights.EnumerateArray().Where(l => settings.LightIds.Contains(l.GetProperty("id").GetString()!)).ToArray();
        if (selected.Length != settings.LightIds.Length || selected.Length == 0 || selected.Any(l => !l.TryGetProperty("color", out _)))
            throw new InvalidOperationException("La sélection Hue a changé. Choisis à nouveau les lampes.");
        _baseline = selected.ToDictionary(l => l.GetProperty("id").GetString()!, Baseline);
        _legacyLights = selected.Select(l => l.TryGetProperty("id_v1", out var legacy) ? legacy.GetString() : null).ToArray();
        store.Put("pending_restore", _baseline);
    }
    public static JsonElement Baseline(JsonElement light)
    {
        var result = new Dictionary<string, object> { ["on"] = light.GetProperty("on").Clone() };
        if (light.TryGetProperty("dimming", out var dim)) result["dimming"] = new { brightness = dim.GetProperty("brightness").GetDouble() };
        if (light.TryGetProperty("color_temperature", out var temp) && temp.TryGetProperty("mirek_valid", out var valid) && valid.ValueKind == JsonValueKind.True && temp.GetProperty("mirek").ValueKind == JsonValueKind.Number)
            result["color_temperature"] = new { mirek = temp.GetProperty("mirek").GetInt32() };
        else if (light.TryGetProperty("color", out var color)) result["color"] = new { xy = color.GetProperty("xy").Clone() };
        if (light.TryGetProperty("gradient", out var gradient) && gradient.GetProperty("points").GetArrayLength() >= 2)
            result["gradient"] = new { points = gradient.GetProperty("points").Clone() };
        if (light.TryGetProperty("effects", out var effects) && effects.TryGetProperty("status", out var status) && status.GetString() is string value)
            result["effects"] = new { effect = value };
        return JsonSerializer.SerializeToElement(result, JsonDefaults.Options);
    }
    public async Task ApplyAsync(RaceFlag flag, EffectSpec effect, AppSettings settings, CancellationToken ct)
    {
        store.Put("pending_restore", _baseline);
        if (effect.Mode == "blink")
        {
            _pulse = new HueNativePulse(client, store, e => Failed?.Invoke(e), time);
            await _pulse.StartAsync(_legacyLights.Select(HueNativePulse.LightId).ToArray(), flag, effect, settings, ct);
            return;
        }
        foreach (var id in settings.LightIds)
        {
            var body = new Dictionary<string, object>
            {
                ["on"] = new { on = true }, ["dimming"] = new { brightness = settings.Brightness / 254.0 * 100 },
                ["color"] = new { xy = new { x = effect.X, y = effect.Y } }, ["dynamics"] = new { duration = (int)(settings.TransitionSeconds * 1000) },
            };
            await client.PutLightAsync(id, body, ct);
            if (settings.LightIds.Length > 1) await Task.Delay(100, ct); // Respect the local REST bridge rate.
        }
    }
    public async Task EndAnimationAsync(CancellationToken ct)
    {
        if (_pulse is not null || store.Get<string[]>("pending_native_alert") is not null)
        {
            _pulse ??= new HueNativePulse(client, store);
            await _pulse.StopAsync(ct); _pulse = null;
        }
        if (store.Get<string>("pending_entertainment") is string area)
        {
            await client.RequestAsync(HttpMethod.Put, "/entertainment_configuration/" + area, new { action = "stop" }, ct);
            store.Delete("pending_entertainment");
        }
    }
    public async Task RestoreAsync(CancellationToken ct)
    {
        List<Exception> errors = [];
        foreach (var (id, state) in _baseline)
            try { await client.PutLightAsync(id, state, ct); }
            catch (Exception e) { errors.Add(e); }
        if (errors.Count > 0) throw new InvalidOperationException("Une lampe n’a pas pu être restaurée : " + errors[0].Message);
        if (_pulse is null && store.Get<string[]>("pending_native_alert") is null && store.Get<string>("pending_entertainment") is null)
            store.Delete("pending_restore");
    }
    public async Task RecoverAsync(CancellationToken ct)
    {
        _baseline = store.Get<Dictionary<string, JsonElement>>("pending_restore") ?? [];
        if (_baseline.Count == 0 && store.Get<string>("pending_entertainment") is null && store.Get<string[]>("pending_native_alert") is null) return;
        await EndAnimationAsync(ct); await RestoreAsync(ct);
    }
    public Task ForgetAsync(CancellationToken ct) { store.Delete("pending_restore"); return Task.CompletedTask; }
}
