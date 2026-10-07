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
        await WriteAsync(new { on = true, bri = settings.Brightness, xy = new[] { effect.X, effect.Y },
            transitiontime = (int)Math.Round(settings.TransitionSeconds * 10), alert = "none" }, ct);
        var alert = flag == RaceFlag.BLUE ? "select" : "lselect";
        await WriteAsync(new { alert }, ct);
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
