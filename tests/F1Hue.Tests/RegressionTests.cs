using System.Net;
using System.Text.Json;
using F1Hue.Core;
using F1Hue.Infrastructure;

internal static class RegressionTests
{
    private static JsonElement J(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    private static JsonElement Message(string flag, string scope = "Sector", string sector = "4") =>
        JsonSerializer.SerializeToElement(new { Messages = new[] { new { Category = "Flag", Flag = flag, Scope = scope, Sector = scope == "Sector" ? sector : null, Utc = "2025-07-06T14:42:05" } } });
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Cancelled(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (OperationCanceledException) { return; }
        throw new Exception("Operation should have been cancelled");
    }
    public static async Task Run(Action<bool, string> check)
    {
        foreach (var (status, expected) in new[] { ("4", "SC"), ("6", "VSC"), ("7", "VSC_ENDING"), ("5", "RED") })
        {
            var parser = new F1Parser();
            parser.Update("SessionInfo", J("{\"Key\":1}"));
            parser.Update("SessionStatus", J("{\"Status\":\"Started\"}"));
            parser.Update("TrackStatus", JsonSerializer.SerializeToElement(new { Status = status }));
            if (status == "5") parser.Update("SessionStatus", J("{\"Status\":\"Aborted\"}"));
            var emitted = new[] { "GREEN", "DOUBLE YELLOW", "BLUE" }.Sum(flag => parser.Update("RaceControlMessages", Message(flag)).Count);
            check(emitted == 0 && parser.State.LastFlag == expected, $"Sector flags cannot replace global {expected}");
        }
        {
            var parser = new F1Parser();
            parser.Update("SessionStatus", J("{\"Status\":\"Started\"}"));
            parser.Update("RaceControlMessages", Message("YELLOW", sector: "4"));
            parser.Update("RaceControlMessages", Message("YELLOW", sector: "5"));
            check(parser.Update("RaceControlMessages", Message("GREEN", sector: "4")).Count == 0 && parser.State.LastFlag == "YELLOW", "Clearing one sector keeps another sector's yellow active");
            var finalGreen = parser.Update("RaceControlMessages", Message("GREEN", sector: "5")).Single();
            check(finalGreen.SourceUtc == new DateTimeOffset(2025, 7, 6, 14, 42, 5, TimeSpan.Zero), "F1 timestamps without a suffix are interpreted as UTC in every timezone");
            parser.Update("TrackStatus", J("{\"Status\":\"4\"}"));
            parser.Update("RaceControlMessages", J("{\"Messages\":[{\"Category\":\"SafetyCar\",\"Message\":\"SAFETY CAR IN THIS LAP\"}]}"));
            parser.Update("TrackStatus", J("{\"Status\":\"4\"}"));
            check(parser.State.LastFlag == "SC_ENDING", "Repeated SC status preserves the announced SC ending");
            parser.Update("RaceControlMessages", Message("GREEN", "Track"));
            check(parser.State.LastFlag == "GREEN", "An explicit global green releases the neutralisation");
            parser.Update("TrackStatus", J("{\"Status\":\"5\"}"));
            parser.Update("SessionInfo", J("{\"Key\":2}"));
            parser.Update("SessionStatus", J("{\"Status\":\"Started\"}"));
            check(parser.Update("RaceControlMessages", Message("BLUE", "Driver")).Single().Value == "BLUE", "Changing sessions resets global flag precedence");
        }
        var temp = Path.Combine(Path.GetTempPath(), "f1hue-regressions-" + Guid.NewGuid());
        try
        {
            using (var store = new Store(temp))
            {
                store.Save(new AppSettings { LightIds = ["11111111-1111-1111-1111-111111111111"] });
                var rejected = false;
                try { using var other = new Store(temp); } catch (InvalidOperationException) { rejected = true; }
                check(rejected, "A second owner cannot open the same profile or recover another instance's lamps");
                var feed = new SessionFeed(); var output = new SessionOutput();
                await using var runner = new Runner(store, feed, output);
                await runner.StartAsync("live");
                feed.Send("session-one");
                await output.Next.Task.WaitAsync(TimeSpan.FromSeconds(2)); output.Next = Signal();
                feed.Send("session-two");
                await output.Next.Task.WaitAsync(TimeSpan.FromSeconds(2));
                check(output.Played == 2, "The same first flag plays in two consecutive sessions");
                await runner.StopAsync();
                var failing = false; var writes = 0;
                var hue = new HueClient(new MemoryVault(), (_, _, _) => new HttpClient(new ReplyHandler(request =>
                {
                    if (request.Method == HttpMethod.Get && failing) throw new HttpRequestException("offline");
                    if (request.Method != HttpMethod.Get) writes++;
                    return new(HttpStatusCode.OK) { Content = new StringContent(request.Method == HttpMethod.Get
                        ? "{\"errors\":[],\"data\":[{\"id\":\"11111111-1111-1111-1111-111111111111\",\"on\":{\"on\":true},\"color\":{\"xy\":{\"x\":0.3,\"y\":0.4}}}]}"
                        : "{\"errors\":[],\"data\":[]}") };
                })) { BaseAddress = new Uri("https://192.168.1.20/") });
                var selectedOutput = new HueOutput(hue, store);
                await selectedOutput.CaptureAsync(store.Read(), CancellationToken.None);
                await selectedOutput.RestoreAsync(CancellationToken.None);
                writes = 0; failing = true;
                try { await selectedOutput.CaptureAsync(store.Read() with { LightIds = ["22222222-2222-2222-2222-222222222222"] }, CancellationToken.None); }
                catch (InvalidOperationException) { }
                await selectedOutput.RestoreAsync(CancellationToken.None);
                check(writes == 0, "A failed capture after changing selection never restores the previous mode's lamps");
            }
            using (var reopened = new Store(temp)) check(reopened.Read().LightIds.Length == 1, "Closing an owner releases its lock and preserves settings");
        }
        finally { Directory.Delete(temp, true); }
        using (var commands = new CommandCoordinator())
        {
            var recovering = Signal(); var release = Signal(); var writes = new List<string>();
            using var startup = commands.Request();
            var recovery = commands.RunAsync(async ct => { writes.Add("restore"); recovering.SetResult(); await release.Task.WaitAsync(ct); return true; }, startup.Token);
            await recovering.Task;
            using var request = commands.Request();
            var start = commands.RunAsync(ct => { writes.Add("effect"); return Task.FromResult(true); }, request.Token);
            check(!start.IsCompleted, "Starting an effect waits until startup recovery has finished");
            release.SetResult(); await recovery; await start;
            check(writes.SequenceEqual(["restore", "effect"]), "Recovery cannot overwrite a newly started effect");
            var pending = Signal();
            using var downloadScope = commands.Request();
            var download = Task.Run(async () => { pending.SetResult(); await Task.Delay(Timeout.Infinite, downloadScope.Token); });
            await pending.Task;
            using var slowRequest = commands.Request();
            var entered = Signal();
            var slow = commands.RunAsync(async ct => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return true; }, slowRequest.Token);
            await entered.Task;
            using var queuedRequest = commands.Request();
            var queued = commands.RunAsync<bool>(_ => throw new Exception("Stale start must never execute"), queuedRequest.Token);
            var stopped = false;
            await commands.StopAsync(() => { stopped = true; return Task.CompletedTask; }).WaitAsync(TimeSpan.FromSeconds(2));
            await Cancelled(download); await Cancelled(slow); await Cancelled(queued);
            check(stopped, "Stop cancels active commands, queued starts and downloads without waiting for their timeout");
            using var next = commands.Request();
            check(await commands.RunAsync(_ => Task.FromResult(true), next.Token), "A fresh command works after Stop");
        }
        using (var http = new HttpClient(new StalledHandler()) { Timeout = TimeSpan.FromMilliseconds(20) })
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await Cancelled(ReplayCatalog.DownloadAsync(http, "https://example.invalid/archive", CancellationToken.None, TimeSpan.FromMilliseconds(100)));
            check(watch.Elapsed < TimeSpan.FromSeconds(1), "Archive deadline also bounds a stalled body after successful headers");
        }
        var replay = ReplayCatalog.Parse("00:00:00.000{\"Status\":\"Started\"}",
            "00:00:01.000{\"Messages\":[{\"Category\":\"Flag\",\"Flag\":\"GREEN\",\"Scope\":\"Sector\",\"Sector\":4}]}",
            "00:00:00.100{\"Status\":\"4\"}\n00:00:02.000{\"Status\":\"1\"}");
        check(replay.Length == 2 && replay[0].Flag == RaceFlag.SC && replay[1].Flag == RaceFlag.GREEN, "Official replay uses TrackStatus and the same sector precedence as live");
    }
    private sealed class SessionFeed : ILiveFeed
    {
        public FeedState State => new(Connected: true, SessionStatus: "Started");
        public event Action<RaceEvent>? Event;
        public void Send(string key) => Event?.Invoke(new("flag", "GREEN", key, "Race", "Race", DateTimeOffset.UtcNow, TimeProvider.System.GetTimestamp()));
    }
    private sealed class SessionOutput : IEffectOutput
    {
        public TaskCompletionSource Next = Signal(); public int Played;
        public Task CaptureAsync(AppSettings s, CancellationToken ct) => Task.CompletedTask;
        public Task ApplyAsync(RaceFlag f, EffectSpec e, AppSettings s, CancellationToken ct) { Played++; Next.TrySetResult(); return Task.CompletedTask; }
        public Task EndAnimationAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RestoreAsync(CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class StalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) });
    }
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { await Task.Delay(Timeout.Infinite, ct); return 0; }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
