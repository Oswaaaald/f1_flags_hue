using System.Net;
using System.Text.Json;
using F1Hue.Core;
using Microsoft.AspNetCore.SignalR.Client;

namespace F1Hue.Infrastructure;

public sealed class F1Feed(Store? store, OperationTimeline? timeline = null) : ILiveFeed
{
    public static readonly string[] Topics = ["RaceControlMessages", "TrackStatus", "SessionInfo", "SessionStatus", "ExtrapolatedClock", "LapCount"];
    public const string HubUrl = "https://livetiming.formula1.com/signalrcore";
    private readonly F1Parser _parser = new();
    private string? _journalError;
    private string? _processingError;
    public FeedState State => _parser.State with { JournalError = Volatile.Read(ref _journalError), ProcessingError = Volatile.Read(ref _processingError) };
    public event Action<RaceEvent>? Event;
    public event Action? Changed;
    public void Simulate(string topic, JsonElement payload)
    {
        _parser.Connection(true);
        Publish(_parser.Update(topic, payload));
    }
    private void Publish(IEnumerable<RaceEvent> events)
    {
        foreach (var item in events)
        {
            // Delivery is independent from the historical journal. Recovery
            // snapshots in HueOutput remain mandatory before any light write.
            timeline?.Add("received", item.EventId, item.Value);
            foreach (var handler in Event?.GetInvocationList() ?? [])
                try
                {
                    ((Action<RaceEvent>)handler)(item);
                }
                catch (Exception) { Volatile.Write(ref _processingError, "Un événement n’a pas pu être transmis. Arrête puis relance le direct ; consulte le diagnostic."); }
            try
            {
                if (store?.Append(item) == true)
                    Volatile.Write(ref _journalError, null);
            }
            catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
            {
                Volatile.Write(ref _journalError, "Le journal ne peut plus être enregistré. Vérifie l’espace disque et les droits du dossier de données.");
                timeline?.Add("journal_error", item.EventId, item.Value, detail: e.GetType().Name);
            }
        }
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
                using (await client.SendAsync(request, ct))
                {
                }
                await using var hub = new HubConnectionBuilder().WithUrl(HubUrl, options => { options.Cookies = cookies; }).Build();
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                hub.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
                var subscription = new F1SubscriptionBuffer(snapshot =>
                {
                    Volatile.Write(ref _processingError, null);
                    Publish(_parser.Snapshot(snapshot));
                }, Process);
                hub.On("feed", new[] { typeof(string), typeof(JsonElement), typeof(string) }, values =>
                {
                    try
                    {
                        subscription.Receive((string)values[0]!, (JsonElement)values[1]!);
                    }
                    catch (Exception e) { closed.TrySetException(e); }
                    return Task.CompletedTask;
                });
                await hub.StartAsync(ct);
                subscription.Complete(await hub.InvokeAsync<JsonElement>("Subscribe", Topics, ct));
                _parser.Connection(true);
                attempt = 0;
                Changed?.Invoke();
                await closed.Task.WaitAsync(ct);
                _parser.Connection(false, "Connexion interrompue. Reconnexion automatique…");
                Changed?.Invoke();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                _parser.Connection(false, e is HttpRequestException ? "Flux F1 inaccessible. Reconnexion automatique…" : "Connexion F1 : " + e.Message);
                Changed?.Invoke();
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(++attempt, 6)))), ct);
            }
            catch (OperationCanceledException) { break; }
        }
        _parser.Connection(false);
        Changed?.Invoke();
    }
    private void Process(string topic, JsonElement payload)
    {
        try
        {
            Publish(_parser.Update(topic, payload));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            Volatile.Write(ref _processingError, "Un message du flux F1 n’a pas pu être décodé. Consulte le diagnostic et vérifie les mises à jour.");
            timeline?.Add("feed_error", detail: topic + ": " + e.GetType().Name);
            Changed?.Invoke();
        }
    }
}
