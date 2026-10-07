using F1Hue.Core;

namespace F1Hue.Infrastructure;

// The bridge renders the same alert=lselect pulse as the original Python engine.
// Only its short lease is renewed; the application never renders fade frames.
public sealed class HueNativePulse(HueClient client, Store store, Action<Exception>? failed = null, TimeProvider? time = null)
{
    private CancellationTokenSource? _cancel;
    private Task _renewal = Task.CompletedTask;
    private HueGroupTarget? _target;
    private string[] _lights = [];
    public static string LightId(string? legacyPath) => HueGroupTarget.LightId(legacyPath);

    public async Task StartAsync(string[] lights, RaceFlag flag, EffectSpec effect, AppSettings settings, CancellationToken ct)
    {
        _target = await HueGroupTarget.ResolveAsync(client, lights, ct);
        _lights = lights.ToArray();
        // Save before sending: a cancelled response may still have reached the bridge.
        store.Put("pending_native_alert", _lights);
        store.Put("pending_native_group", _target.Lease);
        var alert = flag == RaceFlag.BLUE ? "select" : "lselect";
        // Set the flag color immediately, before the bridge's native breathing
        // curve. Fading from a previous color during the first pulse gives
        // intermediate colors. One group command starts every selected lamp.
        await WriteAsync(new { on = true, bri = settings.Brightness, xy = new[] { effect.X, effect.Y },
            transitiontime = 0, effect = "none", alert }, ct);
        _cancel = new CancellationTokenSource();
        // lselect expires on the bridge. Renew within its 15-second lease. Blue
        // used individual select pulses every 0.7 seconds in the Python version.
        _renewal = RenewAsync(alert, flag == RaceFlag.BLUE ? TimeSpan.FromSeconds(.7) : TimeSpan.FromSeconds(10), _cancel.Token);
    }
    private Task WriteAsync(object body, CancellationToken ct) => _target!.WriteAsync(body, ct);
    private async Task RenewAsync(string alert, TimeSpan interval, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(interval, time ?? TimeProvider.System, ct);
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
        if (pending.Length == 0) { store.Delete("pending_native_group"); return; }
        var lease = store.Get<HueGroupLease>("pending_native_group");
        if (lease is not null && pending.ToHashSet().SetEquals(lease.Lights))
        {
            try
            {
                // Normal Stop is one synchronized command, including recovery
                // after restarting. Membership is still checked before writing.
                await HueGroupTarget.FromLease(client, lease).WriteAsync(new { alert = "none" }, ct);
                store.Delete("pending_native_alert"); store.Delete("pending_native_group"); return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (InvalidOperationException) { /* Edited/deleted group: cancel only the saved lamps. */ }
        }
        List<string> remaining = [.. pending]; List<Exception> errors = [];
        // Small selections are dispatched together. Larger recovery batches
        // remain below Hue's recommended ten light commands per second.
        var batches = pending.Chunk(10).ToArray();
        for (var index = 0; index < batches.Length; index++)
        {
            var batch = batches[index];
            var results = await Task.WhenAll(batch.Select(async light =>
            {
                try
                {
                    await client.LegacyRequestAsync(HttpMethod.Put, "lights/" + light + "/state", new { alert = "none" }, ct);
                    return (Light: light, Error: (Exception?)null);
                }
                catch (Exception e) { return (Light: light, Error: e); }
            }));
            foreach (var result in results)
                if (result.Error is null) remaining.Remove(result.Light); else errors.Add(result.Error);
            if (remaining.Count == 0) { store.Delete("pending_native_alert"); store.Delete("pending_native_group"); }
            else store.Put("pending_native_alert", remaining);
            if (index < batches.Length - 1) await Task.Delay(1000, ct);
        }
        if (errors.Count > 0) throw new InvalidOperationException("Impossible d’arrêter la pulsation Hue : " + errors[0].Message);
    }
}
