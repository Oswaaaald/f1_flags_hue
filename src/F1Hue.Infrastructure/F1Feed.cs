using System.Net;
using System.Text.Json;
using F1Hue.Core;
using Microsoft.AspNetCore.SignalR.Client;

namespace F1Hue.Infrastructure;

public sealed class F1Feed(Store? store) : ILiveFeed
{
    public static readonly string[] Topics = ["RaceControlMessages", "TrackStatus", "SessionInfo", "SessionStatus", "ExtrapolatedClock", "LapCount"];
    public const string HubUrl = "https://livetiming.formula1.com/signalrcore";
    private readonly F1Parser _parser = new();
    public FeedState State => _parser.State;
    public event Action<RaceEvent>? Event;
    public event Action? Changed;
    public void Simulate(string topic, JsonElement payload) { _parser.Connection(true); Publish(_parser.Update(topic, payload)); }
    private void Publish(IEnumerable<RaceEvent> events)
    {
        foreach (var item in events) { store?.Append(item); Event?.Invoke(item); }
        Changed?.Invoke();
    }
    public async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cookies = new CookieContainer();
                using (var client = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { Timeout = TimeSpan.FromSeconds(10) })
                using (var request = new HttpRequestMessage(HttpMethod.Options, HubUrl + "/negotiate"))
                using (await client.SendAsync(request, ct)) { }
                await using var hub = new HubConnectionBuilder().WithUrl(HubUrl, options => { options.Cookies = cookies; }).Build();
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                hub.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
                hub.On("feed", new[] { typeof(string), typeof(JsonElement), typeof(string) }, values =>
                {
                    Publish(_parser.Update((string)values[0]!, (JsonElement)values[1]!));
                    return Task.CompletedTask;
                });
                await hub.StartAsync(ct);
                // Subscription result contains the current keyframe, not newly occurring events.
                var snapshot = await hub.InvokeAsync<JsonElement>("Subscribe", Topics, ct);
                Publish(_parser.Snapshot(snapshot));
                _parser.Connection(true); attempt = 0; Changed?.Invoke();
                await closed.Task.WaitAsync(ct);
                _parser.Connection(false, "Connexion interrompue. Reconnexion automatique…"); Changed?.Invoke();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                _parser.Connection(false, e is HttpRequestException ? "Flux F1 inaccessible. Reconnexion automatique…" : "Connexion F1 : " + e.Message);
                Changed?.Invoke();
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(++attempt, 6)))), ct); }
            catch (OperationCanceledException) { break; }
        }
        _parser.Connection(false); Changed?.Invoke();
    }
}
