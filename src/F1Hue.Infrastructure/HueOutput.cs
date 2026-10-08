using System.Text.Json;
using F1Hue.Core;

namespace F1Hue.Infrastructure;

public sealed record RecoveryState(bool Pending, string? BridgeId, string[] LightIds, string[] Resources);

public sealed class HueOutput(HueClient client, Store store, TimeProvider? time = null, OperationTimeline? timeline = null) : IEffectOutput, IEffectFailureSource
{
    public event Action<Exception>? Failed;
    public bool RecoveryPending => RecoveryKeys.Any(key => store.Get<JsonElement?>(key) is not null);
    public static readonly string[] RecoveryKeys = ["pending_restore", "pending_entertainment", "pending_native_alert", "pending_native_group", "hue_snapshot", "hue_owned_group"];
    private Dictionary<string, JsonElement> _baseline = [];
    private HueNativePulse? _pulse;
    private HueGroupTarget? _solidTarget;
    private HueGroupTarget? _preparedTarget;
    private readonly HueSnapshot _snapshot = new(client, store, time);
    private bool _restored;
    private bool _partialRestore;
    private string?[] _legacyLights = [];
    public async Task PrepareAsync(AppSettings settings, CancellationToken ct)
    {
        if (RecoveryPending)
            await RecoverAsync(ct);
        _baseline = [];
        _legacyLights = [];
        _solidTarget = null;
        _preparedTarget = null;
        _restored = false;
        // Starting a live connection does not own the current ambiance yet.
        // The user may change it in Hue while waiting for the first flag.
        var lights = await client.RequestAsync(HttpMethod.Get, "/light", null, ct);
        var selected = lights.EnumerateArray().Where(l => settings.LightIds.Contains(l.GetProperty("id").GetString()!)).ToArray();
        if (selected.Length != settings.LightIds.Length || selected.Length == 0 || selected.Any(l => !l.TryGetProperty("color", out _)))
            throw new InvalidOperationException("La sélection Hue a changé. Choisis à nouveau les lampes.");
        if (selected.Length > 1)
            _preparedTarget = await HueGroupTarget.FindExistingAsync(client, selected.Select(l => HueGroupTarget.LightId(l.TryGetProperty("id_v1", out var id) ? id.GetString() : null)).ToArray(), ct);
    }
    public async Task CaptureAsync(AppSettings settings, CancellationToken ct)
    {
        if (RecoveryPending)
            await RecoverAsync(ct);
        // A failed new capture must not reuse a previous mode's lamps, especially
        // when the user changed the selection while idle.
        _baseline = [];
        _legacyLights = [];
        _solidTarget = null;
        _restored = false;
        var began = (time ?? TimeProvider.System).GetTimestamp();
        timeline?.Add("capture_started");
        var resources = await client.RequestAsync(HttpMethod.Get, settings.LightIds.Length > 1 ? "" : "/light", null, ct);
        var selected = resources.EnumerateArray().Where(l => settings.LightIds.Contains(l.GetProperty("id").GetString()!)
            && (!l.TryGetProperty("type", out var type) || type.GetString() == "light")).ToArray();
        if (selected.Length != settings.LightIds.Length || selected.Length == 0 || selected.Any(l => !l.TryGetProperty("color", out _)))
            throw new InvalidOperationException("La sélection Hue a changé. Choisis à nouveau les lampes.");
        foreach (var light in selected)
            ValidateRestorationCapability(light);
        _baseline = selected.ToDictionary(l => l.GetProperty("id").GetString()!, Baseline);
        _legacyLights = selected.Select(l => l.TryGetProperty("id_v1", out var legacy) ? legacy.GetString() : null).ToArray();
        if (_preparedTarget is not null && !_preparedTarget.Lease.Lights.ToHashSet().SetEquals(_legacyLights.Select(HueGroupTarget.LightId)))
            _preparedTarget = null;
        store.Put("recovery_bridge", (await client.CredentialsAsync(ct)).BridgeId);
        store.Put("pending_restore", _baseline);
        if (_baseline.Count > 1)
            await _snapshot.PrepareAsync(_baseline, ct, resources);
        timeline?.Add("capture_completed", elapsedMs: (time ?? TimeProvider.System).GetElapsedTime(began).TotalMilliseconds);
    }
    public static void ValidateRestorationCapability(JsonElement light)
    {
        // Hue does not expose the playback position of a dynamic palette. Never
        // silently replace an animated ambiance with a frozen approximation.
        if (light.TryGetProperty("dynamics", out var dynamics) && dynamics.TryGetProperty("status", out var dynamicStatus)
            && dynamicStatus.GetString() == "dynamic_palette")
            throw new InvalidOperationException("Arrête l’animation de la scène dans Hue avant le direct : sa progression ne peut pas être restaurée. Les couleurs fixes et les dégradés sont pris en charge.");
        if (light.TryGetProperty("effects_v2", out var effects) && effects.TryGetProperty("status", out var status)
            && status.TryGetProperty("effect", out var effect) && effect.GetString() is not (null or "no_effect"))
        {
            var legacyMatches = light.TryGetProperty("effects", out var legacy) && legacy.TryGetProperty("status", out var old) && old.GetString() == effect.GetString();
            var parameters = status.TryGetProperty("parameters", out var parametersValue) && parametersValue.ValueKind == JsonValueKind.Object && parametersValue.EnumerateObject().Any();
            if (!legacyMatches || parameters)
                throw new InvalidOperationException("Cet effet Hue avancé ne peut pas être restauré fidèlement. Choisis une couleur fixe dans Hue avant de lancer le direct.");
        }
    }
    public static JsonElement Baseline(JsonElement light)
    {
        var result = new Dictionary<string, object> { ["on"] = light.GetProperty("on").Clone() };
        if (light.TryGetProperty("dimming", out var dim))
            result["dimming"] = new
            {
                brightness = dim.GetProperty("brightness").GetDouble()
            };
        if (light.TryGetProperty("color_temperature", out var temp) && temp.TryGetProperty("mirek_valid", out var valid) && valid.ValueKind == JsonValueKind.True && temp.GetProperty("mirek").ValueKind == JsonValueKind.Number)
            result["color_temperature"] = new
            {
                mirek = temp.GetProperty("mirek").GetInt32()
            };
        else if (light.TryGetProperty("color", out var color))
            result["color"] = new
            {
                xy = color.GetProperty("xy").Clone()
            };
        if (light.TryGetProperty("gradient", out var gradient) && gradient.GetProperty("points").GetArrayLength() >= 2)
        {
            var state = new Dictionary<string, object> { ["points"] = gradient.GetProperty("points").Clone() };
            if (gradient.TryGetProperty("mode", out var mode))
                state["mode"] = mode.GetString()!;
            result["gradient"] = state;
        }
        if (light.TryGetProperty("effects", out var effects) && effects.TryGetProperty("status", out var status) && status.GetString() is string value)
            result["effects"] = new
            {
                effect = value
            };
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
            fields.Remove("color");
            fields.Remove("color_temperature");
            fields.Remove("gradient");
        }
        else
        {
            fields.Remove("effects");
            if (fields.ContainsKey("gradient"))
            {
                fields.Remove("color");
                fields.Remove("color_temperature");
            }
        }
        return JsonSerializer.SerializeToElement(fields, JsonDefaults.Options);
    }
    public async Task ApplyAsync(RaceFlag flag, EffectSpec effect, AppSettings settings, CancellationToken ct)
    {
        // Capture at the first actual color change, and recapture after a
        // completed restore so changes made during idle are not overwritten.
        if (_baseline.Count == 0 || _restored)
            await CaptureAsync(settings, ct);
        store.Put("pending_restore", _baseline);
        _restored = false;
        if (effect.Mode == "blink")
        {
            _pulse = new HueNativePulse(client, store, e => Failed?.Invoke(e), time);
            _pulse.UsePreparedTarget(_preparedTarget ?? _solidTarget);
            await _pulse.StartAsync(_legacyLights.Select(HueNativePulse.LightId).ToArray(), flag, effect, settings, ct);
            return;
        }
        if (settings.LightIds.Length > 1)
        {
            _solidTarget ??= _preparedTarget ?? await HueGroupTarget.ResolveAsync(client, _legacyLights.Select(HueGroupTarget.LightId).ToArray(), ct, store);
            await _solidTarget.WriteAsync(new
            {
                on = true,
                bri = settings.Brightness,
                xy = new[] { effect.X, effect.Y },
                transitiontime = (int)Math.Round(settings.TransitionSeconds * 10),
                effect = "none",
                alert = "none"
            }, ct);
            return;
        }
        var state = new Dictionary<string, object>
        {
            ["on"] = new { on = true },
            ["dimming"] = new { brightness = settings.Brightness / 254.0 * 100 },
            ["color"] = new { xy = new { x = effect.X, y = effect.Y } },
            ["dynamics"] = new { duration = (int)(settings.TransitionSeconds * 1000) },
        };
        await client.PutLightAsync(settings.LightIds.Single(), state, ct);
    }
    public async Task EndAnimationAsync(CancellationToken ct)
    {
        if (_pulse is not null || store.Get<string[]>("pending_native_alert") is not null)
        {
            _pulse ??= new HueNativePulse(client, store);
            await _pulse.StopAsync(ct);
            _pulse = null;
            _restored = false;
        }
        if (store.Get<string[]>("pending_native_alert") is null)
            store.Delete("pending_native_group");
        if (store.Get<string>("pending_entertainment") is string area)
        {
            await client.RequestAsync(HttpMethod.Put, "/entertainment_configuration/" + area, new
            {
                action = "stop"
            }, ct);
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
            try
            {
                await _snapshot.RestoreAsync(_baseline, ct);
            }
            catch (HueResourceMissingException)
            {
                // Rebuild only from the saved explicit light IDs. A modified
                // existing scene is not treated as a missing resource.
                await _snapshot.ReleaseAsync(ct);
                await _snapshot.PrepareAsync(_baseline, ct);
                await _snapshot.RestoreAsync(_baseline, ct);
            }
            _restored = true;
        }
        if (!_restored && _baseline.Count == 1)
        {
            var (id, state) = _baseline.Single();
            var confirmed = false;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await client.PutLightAsync(id, state, ct);
                await Task.Delay(TimeSpan.FromMilliseconds(650), time ?? TimeProvider.System, ct);
                if (!await _snapshot.ConfirmedAsync(_baseline, ct))
                    continue;
                await Task.Delay(TimeSpan.FromMilliseconds(350), time ?? TimeProvider.System, ct);
                if (await _snapshot.ConfirmedAsync(_baseline, ct))
                {
                    confirmed = true;
                    break;
                }
            }
            if (!confirmed)
                throw new InvalidOperationException("La restauration de la lampe n’est pas confirmée par le pont. L’état initial est conservé : réessaie Stop.");
        }
        _restored = true;
        timeline?.Add("restore_confirmed", detail: _baseline.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!_partialRestore && _pulse is null && store.Get<string[]>("pending_native_alert") is null && store.Get<string>("pending_entertainment") is null)
            store.Delete("pending_restore");
    }
    public async Task RecoverAsync(CancellationToken ct)
    {
        _baseline = (store.Get<Dictionary<string, JsonElement>>("pending_restore") ?? []).ToDictionary(p => p.Key, p => RestoreState(p.Value));
        _restored = false;
        if (_baseline.Count == 0 && store.Get<string>("pending_entertainment") is null && store.Get<string[]>("pending_native_alert") is null)
        {
            await ReleaseAsync(ct);
            return;
        }
        await EndAnimationAsync(ct);
        await RestoreAsync(ct);
        await ReleaseAsync(ct);
    }
    public Task ForgetAsync(CancellationToken ct)
    {
        store.Delete("pending_restore");
        return Task.CompletedTask;
    }
    public RecoveryState RecoveryStatus() => new(RecoveryPending, store.Get<string>("recovery_bridge"),
        (store.Get<Dictionary<string, JsonElement>>("pending_restore") ?? []).Keys.ToArray(),
        RecoveryKeys.Where(key => store.Get<JsonElement?>(key) is not null).ToArray());
    public async Task RestoreAvailableAsync(CancellationToken ct)
    {
        var original = store.Get<Dictionary<string, JsonElement>>("pending_restore") ?? [];
        var lights = (await client.RequestAsync(HttpMethod.Get, "/light", null, ct)).EnumerateArray().ToArray();
        var available = lights.Select(l => l.GetProperty("id").GetString()!).ToHashSet();
        var selected = original.Where(p => available.Contains(p.Key)).ToDictionary(p => p.Key, p => RestoreState(p.Value));
        if (selected.Count == 0)
            throw new InvalidOperationException("Aucune lampe sauvegardée n’est disponible sur ce pont.");
        store.ArchiveRecovery();
        // Stop renewal first. A missing saved lamp may keep its short native
        // pulse lease, but cannot prevent restoring the other verified lamps.
        try
        {
            await EndAnimationAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException)
        {
            var ids = lights.Where(l => available.Contains(l.GetProperty("id").GetString()!))
                .Select(l => l.TryGetProperty("id_v1", out var id) ? id.GetString() : null).Where(id => id is not null).Select(HueGroupTarget.LightId).ToHashSet();
            var pending = store.Get<string[]>("pending_native_alert") ?? [];
            if (pending.Any(ids.Contains) || store.Get<string>("pending_entertainment") is not null)
                throw;
        }
        await _snapshot.ReleaseAsync(ct);
        _baseline = selected;
        _restored = false;
        _partialRestore = true;
        try
        {
            await RestoreAsync(ct);
            var missing = original.Where(p => !available.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
            if (missing.Count > 0)
                store.Put("pending_restore", missing);
            else
                store.Delete("pending_restore");
            await ReleaseAsync(ct);
        }
        finally { _partialRestore = false; }
    }
    public async Task<string> AbandonRecoveryAsync(CancellationToken ct)
    {
        // Renewal must be stopped locally even if the bridge is unreachable.
        if (_pulse is not null)
            await _pulse.CancelRenewalAsync(ct);
        var archive = store.ArchiveRecovery(discard: true);
        _pulse = null;
        _baseline = [];
        _solidTarget = null;
        _legacyLights = [];
        _restored = true;
        return archive;
    }
    public async Task ReleaseAsync(CancellationToken ct)
    {
        await _snapshot.ReleaseAsync(ct);
        await HueGroupTarget.ReleaseOwnedAsync(client, store, ct);
        if (!RecoveryPending)
            store.Delete("recovery_bridge");
    }
}
