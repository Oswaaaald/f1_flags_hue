using System.Text.Json;
using F1Hue.Core;
using F1Hue.Infrastructure;

var checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
void Throws(Action action, string message) { try { action(); } catch (Exception e) when (e is ArgumentException or JsonException or InvalidOperationException) { Check(true, message); return; } throw new Exception("Expected rejection: " + message); }
async Task Until(Func<bool> condition) { for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10); if (!condition()) throw new Exception("Timed out waiting for outcome"); }
JsonElement J(string value) => JsonSerializer.Deserialize<JsonElement>(value);
var settings = new AppSettings();
Check(settings.Validate().Effects.Count == 9 && !settings.Effects[RaceFlag.BLUE].Enabled, "Nine effects and blue disabled by default");
Throws(() => (settings with { OffsetSeconds = double.NaN }).Validate(), "Reject non-finite offset");
Throws(() => (settings with { Brightness = 0 }).Validate(), "Reject invalid brightness");
Throws(() => (settings with { LightIds = ["1"] }).Validate(), "Legacy light IDs cannot target v2 lamps");
Throws(() => SettingsPatch.Apply(settings, J("{\"username\":\"secret\"}")), "Settings cannot accept Hue credentials");
Throws(() => SettingsPatch.Apply(settings, J("{\"lightIds\":[]}")), "Settings cannot bypass target validation");
Throws(() => SettingsPatch.Apply(settings, J("{\"effects\":{\"PURPLE\":{\"enabled\":true}}}")), "Unknown flags rejected");
Throws(() => SettingsPatch.Apply(settings, J("{\"effects\":{\"BLUE\":{\"enabled\":\"true\"}}}")), "Enabled requires a real boolean");
Throws(() => SettingsPatch.Apply(settings, J("{\"effects\":{\"GREEN\":{\"durationSeconds\":0}}}")), "Fixed duration must be positive");
var patched = SettingsPatch.Apply(settings, J("{\"effects\":{\"BLUE\":{\"enabled\":true,\"durationSeconds\":null}}}"));
Check(patched.Effects[RaceFlag.BLUE].Enabled && patched.Effects[RaceFlag.BLUE].DurationSeconds is null && !settings.Effects[RaceFlag.BLUE].Enabled, "Patching preserves original and all unrelated settings");
var temp = Path.Combine(Path.GetTempPath(), "f1hue-tests-" + Guid.NewGuid());
try
{
    using var store = new Store(temp);
    var id = "11111111-1111-1111-1111-111111111111";
    store.Save(patched with
    {
        LightIds = [id],
        OffsetSeconds = 41.5
    });
    Check(store.Read().LightIds.SequenceEqual([id]) && store.Read().OffsetSeconds == 41.5, "SQLite preserves targets and calibration");
    var sample = new RaceEvent("flag", "RED", "test' OR 1=1", "Qualifying", "Qualifying", DateTimeOffset.UtcNow, TimeProvider.System.GetTimestamp());
    store.Append(sample with
    {
        Initial = true
    });
    Check(store.Events().Length == 0, "Snapshot events are not added to the journal");
    store.Append(sample);
    Check(store.Events("test' OR 1=1").Length == 1 && store.Events("missing").Length == 0, "Journal uses literal session keys and preserves events");
    Throws(() => { using var other = new Store(temp); }, "An active profile refuses a second database owner");
    var imported = LegacyImport.Parse("""
        bridge_ip: 192.168.1.20
        username: legacy-test-key
        light_ids: ['1']
        sync:
          offset_seconds: 41.5
        flags:
          ignore_blue: true
          enabled:
            BLUE: true
        patterns:
          GREEN:
            mode: solid
            duration_seconds: 3.5
          SC:
            mode: blink
            duration_seconds: null
        """);
    Check(imported.Settings.OffsetSeconds == 41.5 && imported.Bridge?.ApplicationKey == "legacy-test-key", "Legacy importer preserves TV offset and bridge key");
    Check(imported.Settings.Effects[RaceFlag.GREEN].DurationSeconds == 3.5 && imported.Settings.Effects[RaceFlag.BLUE].Enabled && imported.Settings.Effects[RaceFlag.SC].DurationSeconds is null, "Legacy effect overrides survive import");
    Check(!JsonSerializer.Serialize(imported.Settings).Contains("legacy-test-key"), "Public settings never contain bridge key");
    var inv = new HueInventory([new(id, "Test", true, "/lights/1")], [new("22222222-2222-2222-2222-222222222222", "Room", "room", [id], "/groups/1")], []);
    Check(HueClient.ResolveSelection(inv, [], [inv.Groups[0].Id]).SequenceEqual([id]), "Group resolves to explicit lamp IDs");
    Throws(() => HueClient.ResolveSelection(inv, [], []), "Empty selection never means all lamps");
    Throws(() => HueClient.ResolveSelection(inv, ["missing"], []), "Missing lamp never broadens selection");
    Throws(() => HueClient.ResolveSelection(inv, [], ["missing"]), "Missing group never broadens selection");
    store.Put("legacy_selection", new LegacySelection(["1"], []));
    Check(LegacyImport.ResolveSelection(store, inv) && store.Read().LightIds.SequenceEqual([id]), "Legacy IDs migrate to exact v2 IDs");
    store.Put("legacy_selection", new LegacySelection([], ["0"]));
    Check(!LegacyImport.ResolveSelection(store, inv), "Legacy all-lights group requires explicit choice");
    var output = new FakeOutput();
    var source = new FakeFeed();
    var liveSettings = new AppSettings { LightIds = [id], OffsetSeconds = .18 };
    store.Save(liveSettings);
    await using (var runner = new Runner(store, source, output))
    {
        await runner.StartAsync("live");
        source.Send("flag", "GREEN");
        await Task.Delay(40);
        Check(output.Colors.Count == 0, "TV delay is applied before an event reaches the lamps");
        store.Save(SettingsPatch.Apply(store.Read(), J("{\"effects\":{\"GREEN\":{\"enabled\":false}}}")));
        await Task.Delay(220);
        Check(output.Colors.Count == 0 && output.Restores > 0, "Queued event reads latest enable setting after its TV delay");
        store.Save(SettingsPatch.Apply(store.Read(), J("{\"effects\":{\"BLUE\":{\"enabled\":true,\"durationSeconds\":0.1}}}")));
        source.Send("flag", "BLUE");
        await Until(() => output.Colors.Contains(RaceFlag.BLUE));
        Check(runner.State.Running, "Enabling blue works without reconnecting the live source");
        await Until(() => runner.State.ActiveEffect is null);
        Check(output.Restores >= 2, "Fixed duration restores selected baseline during a live session");
        source.Send("session_status", "Finished");
        store.Save(SettingsPatch.Apply(store.Read(), J("{\"exitOnChequered\":true,\"effects\":{\"CHEQUERED\":{\"durationSeconds\":0.1}}}")));
        source.Send("flag", "CHEQUERED");
        await Task.Delay(40);
        Check(runner.State.Running && !output.Colors.Contains(RaceFlag.CHEQUERED), "Finished does not discard the delayed chequered flag");
        await Until(() => !runner.State.Running);
        Check(output.Colors.Last() == RaceFlag.CHEQUERED, "Auto-stop waits until delayed chequered effect completes");
    }
    store.Save(liveSettings with
    {
        OffsetSeconds = 0
    });
    var currentSource = new FakeFeed { State = new(Connected: true, SessionKey: "test", SessionStatus: "Started", LastFlag: "SC") };
    var currentOutput = new FakeOutput();
    await using (var resumed = new Runner(store, currentSource, currentOutput))
    {
        await resumed.StartAsync("live");
        await Until(() => currentOutput.Colors.Contains(RaceFlag.SC));
        Check(true, "Starting during a session synchronizes the current flag");
        await resumed.StopAsync();
    }
    currentSource.State = currentSource.State with
    {
        SessionStatus = "Ends"
    };
    currentOutput = new FakeOutput();
    await using (var ended = new Runner(store, currentSource, currentOutput))
    {
        await ended.StartAsync("live");
        await Task.Delay(20);
        Check(currentOutput.Colors.Count == 0, "Starting after a finished session does not replay its final flag");
        await ended.StopAsync();
    }
    var effectOutput = new FakeOutput();
    await using (var engine = new EffectEngine(effectOutput))
    {
        var quick = SettingsPatch.Apply(new(), J("{\"effects\":{\"GREEN\":{\"durationSeconds\":0.12}}}"));
        await engine.PlayAsync(RaceFlag.GREEN, quick);
        await Task.Delay(35);
        await engine.PlayAsync(RaceFlag.RED, quick);
        await Task.Delay(180);
        Check(engine.Active == RaceFlag.RED && effectOutput.Restores == 0, "Superseded duration cannot restore over a newer effect");
        await engine.PlayAsync(RaceFlag.BLUE, quick);
        Check(engine.Active is null && effectOutput.Restores == 1 && !effectOutput.Colors.Contains(RaceFlag.BLUE), "Disabled flag ends previous effect without playing its own color");
        await engine.PlayAsync(RaceFlag.BLUE, quick, preview: true);
        Check(engine.Active == RaceFlag.BLUE, "Individual preview can show a disabled event");
        await engine.StopAsync(false);
        Check(effectOutput.Restores == 1, "Restoration preference is respected on explicit stop");
    }
    effectOutput = new FakeOutput();
    foreach (var stopping in new[] { false, true })
    {
        var confirming = new PendingConfirmationOutput();
        await using var engine = new EffectEngine(confirming);
        var shortFlag = settings with
        {
            Effects = new(settings.Effects)
            {
                [RaceFlag.GREEN] = settings.Effects[RaceFlag.GREEN] with
                {
                    DurationSeconds = .1
                }
            }
        };
        await engine.PlayAsync(RaceFlag.GREEN, shortFlag);
        await confirming.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (stopping)
            await engine.StopAsync(false).WaitAsync(TimeSpan.FromSeconds(1));
        else
            await engine.PlayAsync(RaceFlag.RED, settings).WaitAsync(TimeSpan.FromSeconds(1));
        Check(confirming.Cancelled && engine.Active == (stopping ? null : RaceFlag.RED), stopping
            ? "Stop cancels an expired flag's pending restoration confirmation before acquiring the effect gate"
            : "A newer flag preempts restoration confirmation without waiting for stale retries or clearing the new effect");
    }
    await using (var engine = new EffectEngine(effectOutput))
    {
        await engine.PlayAsync(RaceFlag.RED, settings);
        effectOutput.FailEnd = true;
        try
        {
            await engine.StopAsync(true);
        }
        catch (InvalidOperationException) { }
        Check(effectOutput.Restores == 1, "Restoration is attempted even when stopping an animation fails");
        effectOutput.FailEnd = false;
    }
    var scOutput = new FakeOutput();
    await using (var scRunner = new Runner(store, new FakeFeed(), scOutput))
    {
        for (var i = 0; i < 5; i++)
        {
            await scRunner.StartAsync("preview", RaceFlag.SC);
            await Until(() => scRunner.State.ActiveEffect == "SC");
            await scRunner.StopAsync().WaitAsync(TimeSpan.FromSeconds(1));
        }
        Check(!scRunner.State.Running && !scRunner.State.CleanupPending && scOutput.Restores == 5,
            "Repeated indefinite Safety Car previews stop promptly and restore every time");
        await scRunner.StartAsync("preview", RaceFlag.SC);
        await Until(() => scRunner.State.ActiveEffect == "SC");
        scOutput.FailEnd = true;
        var rejected = false;
        try
        {
            await scRunner.StopAsync();
        }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected && scRunner.State.CleanupPending && scRunner.State.Error is not null,
            "A failed stop is reported to its caller and remains retryable");
        rejected = false;
        try
        {
            await scRunner.StartAsync("preview", RaceFlag.RED);
        }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "A new mode cannot overwrite the baseline after an incomplete stop");
        scOutput.FailEnd = false;
        await scRunner.StopAsync();
        Check(!scRunner.State.CleanupPending && scRunner.State.Error is null,
            "Retrying Stop finishes cleanup and clears the error");
    }
    var requests = new List<(string Path, string Body)>();
    var stopFailures = 1;
    var recoveryClient = new HueClient(new MemoryVault(), (_, _, _) => new HttpClient(new ReplyHandler(request =>
    {
        var requestPath = request.RequestUri!.AbsolutePath;
        requests.Add((requestPath, request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? ""));
        return new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(
            requestPath.Contains("entertainment_configuration") && stopFailures-- > 0
                ? "{\"errors\":[{\"description\":\"temporarily unavailable\"}],\"data\":[]}"
                : request.Method == HttpMethod.Get ? "{\"errors\":[],\"data\":[{\"id\":\"" + id + "\",\"on\":{\"on\":true},\"dimming\":{\"brightness\":42}}]}" : "{\"errors\":[],\"data\":[]}", System.Text.Encoding.UTF8, "application/json")
        };
    }))
    {
        BaseAddress = new Uri("https://192.168.1.20/")
    });
    var pendingArea = "22222222-2222-2222-2222-222222222222";
    store.Put("pending_restore", new Dictionary<string, JsonElement> { [id] = J("{\"on\":{\"on\":true},\"dimming\":{\"brightness\":42}}") });
    store.Put("pending_entertainment", pendingArea);
    var hueOutput = new HueOutput(recoveryClient, store);
    try
    {
        await hueOutput.RecoverAsync(CancellationToken.None);
    }
    catch (InvalidOperationException) { }
    Check(store.Get<string>("pending_entertainment") == pendingArea && store.Get<Dictionary<string, JsonElement>>("pending_restore") is not null,
        "Failed Entertainment release keeps the area and original baseline for recovery");
    await hueOutput.RecoverAsync(CancellationToken.None);
    Check(requests.Count(r => r.Path.Contains("entertainment_configuration")) == 2
        && requests.Last(r => r.Body.Length > 0).Body.Contains("42") && store.Get<string>("pending_entertainment") is null
        && store.Get<Dictionary<string, JsonElement>>("pending_restore") is null,
        "Recovery retries Entertainment release before restoring only the saved lamp");
    Check(!requests.Any(r => r.Body.Contains("signaling")),
        "Entertainment recovery restores saved lamps without sending any native signal command");
    await hueOutput.ForgetAsync(CancellationToken.None);
    requests.Clear();
    var signalsRejectedClient = new HueClient(new MemoryVault(), (_, _, _) => new HttpClient(new ReplyHandler(request =>
    {
        var requestPath = request.RequestUri!.AbsolutePath;
        var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
        requests.Add((requestPath, body));
        // Reproduce a bridge that advertises signaling but rejects the old no_signal command.
        if (body.Contains("signaling"))
            return new(System.Net.HttpStatusCode.BadRequest);
        var response = request.Method == HttpMethod.Get
            ? "{\"errors\":[],\"data\":[{\"id\":\"" + id + "\",\"on\":{\"on\":true},\"dimming\":{\"brightness\":42},\"color\":{\"xy\":{\"x\":0.3,\"y\":0.4}},\"signaling\":{\"signal_values\":[\"no_signal\",\"on_off_color\"]}}]}"
            : "{\"errors\":[],\"data\":[]}";
        return new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(response, System.Text.Encoding.UTF8, "application/json")
        };
    }))
    {
        BaseAddress = new Uri("https://192.168.1.20/")
    });
    var signalsRejectedOutput = new HueOutput(signalsRejectedClient, store);
    await signalsRejectedOutput.CaptureAsync(liveSettings, CancellationToken.None);
    await using (var engine = new EffectEngine(signalsRejectedOutput))
    {
        await engine.PlayAsync(RaceFlag.RED, liveSettings);
        await engine.StopAsync(true);
    }
    Check(!requests.Any(r => r.Body.Contains("signaling")) && requests.Last(r => r.Body.Length > 0).Body.Contains("42")
        && store.Get<Dictionary<string, JsonElement>>("pending_restore") is null,
        "Capture, playback and Stop succeed when the bridge rejects native signaling, preserving restoration");
    requests.Clear();
    store.Put("pending_restore", new Dictionary<string, JsonElement> { [id] = J("{\"on\":{\"on\":true},\"dimming\":{\"brightness\":42}}") });
    await new HueOutput(signalsRejectedClient, store).RecoverAsync(CancellationToken.None);
    Check(requests.Count == 3 && requests[0].Body.Contains("42") && !requests[0].Body.Contains("signaling")
        && store.Get<Dictionary<string, JsonElement>>("pending_restore") is null,
        "A baseline left pending by preview.2 recovers directly without repeating its failing signal cancellation");
}
finally { Directory.Delete(temp, true); }
var parser = new F1Parser();
parser.Snapshot(J("{\"SessionInfo\":{\"Key\":1,\"Name\":\"Practice 1\",\"Type\":\"Practice\"},\"SessionStatus\":{\"Status\":\"Started\"},\"TrackStatus\":{\"Status\":\"1\"}}"));
Check(parser.State.LastFlag == "GREEN", "Official snapshot decodes current track status");
Check(parser.Update("TrackStatus", J("{\"Status\":\"1\"}")).Count == 0, "Duplicate track flags are suppressed");
parser.Update("SessionStatus", J("{\"Status\":\"Aborted\"}"));
Check(parser.Update("TrackStatus", J("{\"Status\":\"5\"}")).Single().Value == "RED", "Red flag is accepted after Aborted");
Check(parser.Update("RaceControlMessages", J("{\"_kf\":true,\"Messages\":[{\"Category\":\"Flag\",\"Flag\":\"YELLOW\"}]}")).Count == 0, "Race-control keyframe is not replayed");
parser.Update("SessionStatus", J("{\"Status\":\"Finished\"}"));
Check(parser.Update("RaceControlMessages", J("{\"Messages\":{\"0\":{\"Category\":\"Flag\",\"Flag\":\"CHEQUERED\",\"Utc\":\"2026-10-07T12:00:00Z\"}}}")).Single().SourceUtc is not null, "Chequered flag accepted after Finished with source timestamp");
Check(parser.Update("TrackStatus", J("{\"Status\":\"1\"}")).Count == 0, "Finished session cannot flash an old green track flag");
parser.Update("SessionInfo", J("{\"Key\":2,\"Name\":\"Race\",\"Type\":\"Race\"}"));
Check(parser.State.LastFlag is null && parser.State.SessionStatus is null, "New session clears stale state");
parser.Update("SessionStatus", J("{\"Status\":\"Started\"}"));
Check(parser.Update("RaceControlMessages", J("{\"Messages\":[{\"Category\":\"Flag\",\"Flag\":\"BLUE\"}]}")).Single().Value == "BLUE", "Feed emits blue independently of current display settings");
Check(parser.Update("RaceControlMessages", J("{\"Messages\":[{\"Category\":\"SafetyCar\",\"Message\":\"SAFETY CAR IN THIS LAP\"}]}")).Single().Value == "SC_ENDING", "Race-control safety car ending is decoded");
parser.Update("LapCount", J("{\"CurrentLap\":12,\"TotalLaps\":53}"));
parser.Update("LapCount", J("{\"CurrentLap\":13}"));
Check(parser.State.TotalLaps == 53 && parser.State.CurrentLap == 13, "Partial lap updates retain total lap count");
var now = DateTimeOffset.UtcNow;
Check(Calibration.ParseRemaining("1:02:03") == 3723, "Session clock handles hours");
Check(Calibration.Compare(new(now, "10:00", true), "11:00", now) == 60.5, "TV countdown computes positive broadcast offset");
Throws(() => Calibration.Remaining(new(now, "00:00", true), now), "Expired session clock cannot calibrate");
Throws(() => Calibration.Remaining(new(now, "12:00", false), now), "Paused session clock cannot calibrate");
Throws(() => Calibration.ParseRemaining("12:99"), "Malformed TV clock rejected");
var calibrationFeed = new FakeFeed();
var cal = new CalibrationSession(calibrationFeed);
cal.Arm("start");
calibrationFeed.Send("session_status", "Started", initial: true);
Check(cal.State.Waiting, "Calibration ignores snapshot start status");
calibrationFeed.Send("session_status", "Started");
Check(!cal.State.Waiting && cal.Seen() >= 0, "Race start click produces a monotonic offset");
cal.Arm("lap");
calibrationFeed.Send("lap", "5");
Check(cal.State.Reference?.Value == "5", "Lap calibration retains the exact reference lap");
Throws(() => HueClient.LocalAddress("127.0.0.1"), "Bridge URL cannot target loopback");
Throws(() => HueClient.LocalAddress("8.8.8.8"), "Bridge URL cannot target public networks");
Throws(() => HueClient.LocalAddress("192.168.1.1:80/path"), "Bridge IP cannot inject ports or paths");
Check(HueClient.LocalAddress("192.168.1.20") == "192.168.1.20", "Private IPv4 bridge accepted");
Check(HueDiscovery.ParseAddresses([0, 1, 2]).Length == 0, "Truncated mDNS packet is harmless");
var baseline = HueOutput.Baseline(J("{\"id\":\"x\",\"on\":{\"on\":false},\"dimming\":{\"brightness\":42,\"min_dim_level\":0.1},\"color\":{\"xy\":{\"x\":0.3,\"y\":0.4}},\"color_temperature\":{\"mirek\":250,\"mirek_valid\":true}}"));
Check(!baseline.TryGetProperty("id", out _) && !baseline.TryGetProperty("color", out _) && baseline.GetProperty("color_temperature").GetProperty("mirek").GetInt32() == 250, "Restoration strips read-only fields and preserves white temperature");
var replay = ReplayCatalog.Parse("00:00:00.000{\"Status\":\"Started\"}\n00:00:02.000{\"Status\":\"Finished\"}", "00:00:01.000{\"Messages\":[{\"Category\":\"Flag\",\"Flag\":\"GREEN\"}]}\n00:00:03.000{\"Messages\":[{\"Category\":\"Flag\",\"Flag\":\"CHEQUERED\"}]}");
Check(replay.Length == 2 && replay[1].Flag == RaceFlag.CHEQUERED && replay[1].AtSeconds == 2, "Archive replay preserves a late chequered flag and relative timing");
var testVault = new MemoryVault();
var refused = new HueClient(testVault, (_, pin, _) => new HttpClient(new ReplyHandler(request =>
{
    Check(pin == "test-pin" && request.Headers.GetValues("hue-application-key").Single() == "private-test-key", "Hue requests use pinned bridge and application-key header");
    return new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent("{\"errors\":[{\"description\":\"resource unavailable private-test-key\"}],\"data\":[]}", System.Text.Encoding.UTF8, "application/json")
    };
}))
{
    BaseAddress = new Uri("https://192.168.1.20/")
});
try
{
    await refused.PutLightAsync("11111111-1111-1111-1111-111111111111", new
    {
        on = new
        {
            on = true
        }
    }, CancellationToken.None);
    throw new Exception("Expected Hue error");
}
catch (InvalidOperationException e) { Check(e.Message.Contains("refusée") && !e.Message.Contains("private-test-key"), "Hue errors are rejected and secrets redacted"); }
var unauthorized = new HueClient(testVault, (_, _, _) => new HttpClient(new ReplyHandler(_ => new(System.Net.HttpStatusCode.Forbidden))) { BaseAddress = new Uri("https://192.168.1.20/") });
try
{
    await unauthorized.InventoryAsync(CancellationToken.None);
    throw new Exception("Expected re-pair error");
}
catch (InvalidOperationException e) { Check(e.Message.Contains("Lie à nouveau"), "Expired Hue key has an actionable pairing error"); }
foreach (var statusCode in new[] { System.Net.HttpStatusCode.BadRequest, System.Net.HttpStatusCode.NotFound, System.Net.HttpStatusCode.TooManyRequests })
{
    var httpFailure = new HueClient(testVault, (_, _, _) => new HttpClient(new ReplyHandler(_ => new(statusCode))) { BaseAddress = new Uri("https://192.168.1.20/") });
    try
    {
        await httpFailure.InventoryAsync(CancellationToken.None);
        throw new Exception("Expected HTTP rejection");
    }
    catch (InvalidOperationException e) { Check(e.Message.Contains($"HTTP {(int)statusCode}") && !e.Message.Contains("certificat") && !e.Message.Contains("inaccessible"), $"HTTP {(int)statusCode} remains a bridge rejection, not a false network/certificate failure"); }
}
foreach (var error in new[] { HttpRequestError.ConnectionError, HttpRequestError.SecureConnectionError })
{
    var transportFailure = new HueClient(testVault, (_, _, _) => new HttpClient(new ReplyHandler(_ => throw new HttpRequestException(error, "private internal detail"))) { BaseAddress = new Uri("https://192.168.1.20/") });
    try
    {
        await transportFailure.InventoryAsync(CancellationToken.None);
        throw new Exception("Expected transport failure");
    }
    catch (InvalidOperationException e) { Check(e.Message.Contains(error == HttpRequestError.SecureConnectionError ? "HTTPS" : "inaccessible") && !e.Message.Contains("private internal detail"), $"{error} gets an accurate sanitized error"); }
}
var pairs = new List<string?>();
var pairClient = new HueClient(testVault, (_, pin, capture) =>
{
    pairs.Add(pin);
    if (capture is not null)
        capture("new-pin");
    return new HttpClient(new ReplyHandler(request => new(System.Net.HttpStatusCode.OK) { Content = new StringContent(request.Method == HttpMethod.Get ? "{\"bridgeid\":\"ECB5FAFFFE817F1B\",\"name\":\"Bridge test\"}" : "[{\"error\":{\"type\":101,\"description\":\"link button not pressed\"}}]", System.Text.Encoding.UTF8, "application/json") })) { BaseAddress = new Uri("https://192.168.1.20/") };
});
try
{
    await pairClient.PairAsync("192.168.1.20", CancellationToken.None);
    throw new Exception("Expected physical-button error");
}
catch (InvalidOperationException e) { Check(e.Message.Contains("bouton physique") && pairs.SequenceEqual(new string?[] { null, "new-pin" }), "Pairing requires physical button and pins before credential creation"); }
await NativePulseTests.Run(Check);
await GroupColorTests.Run(Check);
await SnapshotTests.Run(Check);
await RegressionTests.Run(Check);
await DesktopAccessTests.Run(Check);
await AuditRegressionTests.Run(Check);
await HueRecoveryAuditTests.Run(Check);
Console.WriteLine($"\n{checks} checks passed.");

sealed class FakeOutput : IEffectOutput
{
    public readonly System.Collections.Concurrent.ConcurrentQueue<RaceFlag> Colors = new(); public int Restores; public bool FailEnd;
    public Task CaptureAsync(AppSettings s, CancellationToken ct) => Task.CompletedTask;
    public Task ApplyAsync(RaceFlag flag, EffectSpec e, AppSettings s, CancellationToken ct)
    {
        Colors.Enqueue(flag);
        return Task.CompletedTask;
    }
    public Task EndAnimationAsync(CancellationToken ct) => FailEnd ? throw new InvalidOperationException("fake bridge offline") : Task.CompletedTask;
    public Task RestoreAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref Restores);
        return Task.CompletedTask;
    }
}
sealed class PendingConfirmationOutput : IEffectOutput
{
    public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Cancelled;
    public Task CaptureAsync(AppSettings s, CancellationToken ct) => Task.CompletedTask;
    public Task ApplyAsync(RaceFlag flag, EffectSpec e, AppSettings s, CancellationToken ct) => Task.CompletedTask;
    public Task EndAnimationAsync(CancellationToken ct) => Task.CompletedTask;
    public async Task RestoreAsync(CancellationToken ct)
    {
        Entered.TrySetResult();
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { Cancelled = true; throw; }
    }
}
sealed class FakeFeed : ILiveFeed
{
    public FeedState State { get; set; } = new(Connected: true, SessionKey: "test", SessionType: "Race");
    public event Action<RaceEvent>? Event;
    public void Send(string kind, string value, bool initial = false) => Event?.Invoke(new(kind, value, "test", "Race", "Race", DateTimeOffset.UtcNow, TimeProvider.System.GetTimestamp(), Initial: initial));
}

sealed class MemoryVault : IBridgeVault
{
    private BridgeCredentials? _credentials = new("192.168.1.20", "private-test-key", CertificatePin: "test-pin");
    public BridgeCredentials? Read() => _credentials;
    public void Save(BridgeCredentials c) => _credentials = c;
    public void Clear() => _credentials = null;
}
sealed class ReplyHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(reply(request));
}
