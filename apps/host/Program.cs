using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using static F1Hue.Host.ApiInput;
using F1Hue.Core;
using F1Hue.Host;
using F1Hue.Infrastructure;

var version = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(F1Parser).Assembly)!.InformationalVersion.Split('+')[0];
if (args.Contains("--version")) { Console.WriteLine(version); return; }
if (args.Contains("--runtime-info"))
{
    using var database = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
    database.Open();
    using var command = database.CreateCommand();
    command.CommandText = "SELECT sqlite_version()";
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        version,
        dotnet = Environment.Version.ToString(),
        sqlite = command.ExecuteScalar(),
        architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()
    }, JsonDefaults.Options));
    return;
}
string? Option(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
if (Option("--export-contract") is string contract) { File.WriteAllText(contract, ContractExporter.TypeScript()); return; }
if (args.Contains("--health-check"))
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    try
    {
        using var response = await http.GetAsync(Environment.GetEnvironmentVariable("F1_HUE_HEALTH_URL") ?? "http://127.0.0.1:" + (Environment.GetEnvironmentVariable("F1_HUE_PORT") ?? "8080") + "/health");
        Environment.ExitCode = response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (HttpRequestException) { Environment.ExitCode = 1; }
    catch (TaskCanceledException) { Environment.ExitCode = 1; }
    return;
}
if (args.Contains("--check-live"))
{
    var diagnosticFeed = new F1Feed(null);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    diagnosticFeed.Changed += () => { if (diagnosticFeed.State.Connected) deadline.Cancel(); };
    await diagnosticFeed.RunAsync(deadline.Token);
    var success = diagnosticFeed.State.LastDataAt is not null;
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        connected = success,
        diagnosticFeed.State.SessionName,
        diagnosticFeed.State.SessionStatus,
        diagnosticFeed.State.LastDataAt,
        diagnosticFeed.State.LastError
    }, JsonDefaults.Options));
    Environment.ExitCode = success ? 0 : 1;
    return;
}
var desktopMode = args.Contains("--desktop");
var listen = Option("--listen") ?? Environment.GetEnvironmentVariable("F1_HUE_LISTEN") ?? "127.0.0.1";
if (!IPAddress.TryParse(listen, out _)) throw new ArgumentException("--listen attend une adresse IP.");
if (desktopMode && listen != "127.0.0.1") { Console.Error.WriteLine("La connexion automatique de bureau exige --listen 127.0.0.1."); Environment.ExitCode = 2; return; }
var data = Option("--data") ?? Environment.GetEnvironmentVariable("F1_HUE_DATA_DIR") ?? PrivateFiles.DefaultDataPath;
if (Option("--restore-backup") is string backup) { Console.WriteLine("Sauvegarde restaurée. Copie de sécurité : " + Store.RestoreProfileBackup(data, backup)); return; }
Store ownedStore;
try
{
    ownedStore = new Store(data);
}
catch (InvalidOperationException e) { Console.Error.WriteLine(e.Message); Environment.ExitCode = 2; return; }
using var store = ownedStore;
if (args.Contains("--backup")) { Console.WriteLine(store.Backup()); return; }
var vault = new SecretVault(store.DirectoryPath);
using var desktopAccess = desktopMode ? new DesktopAccess(store.DirectoryPath) : null;
var auth = new Auth(store, desktopAccess);
if (args.Contains("--reset-password")) { auth.Reset(); Console.WriteLine("Accès réinitialisé. Code de configuration : " + auth.SetupFile); return; }
if (Option("--import") is string legacy) Console.WriteLine(LegacyImport.Import(legacy, store, vault) ? "Configuration existante importée." : "Configuration déjà présente : import ignoré.");
var simulate = args.Contains("--simulate") || Environment.GetEnvironmentVariable("F1_HUE_SIMULATE") == "1";
var timeline = new OperationTimeline();
var feed = new F1Feed(store, timeline);
using var hue = new HueClient(vault);
if (args.Contains("--check-hue"))
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    try
    {
        var inventory = await hue.InventoryAsync(deadline.Token);
        LegacyImport.ResolveSelection(store, inventory);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            connected = true,
            lights = inventory.Lights.Length,
            groups = inventory.Groups.Length,
            selected = store.Read().LightIds.Length,
            entertainmentAreas = inventory.Entertainment.Length
        }, JsonDefaults.Options));
    }
    catch (Exception e) { Console.WriteLine("Vérification Hue : " + e.Message); Environment.ExitCode = 1; }
    return;
}
IEffectOutput output = simulate ? new SimulationOutput() : new HueOutput(hue, store, timeline: timeline);
await using var runner = new Runner(store, feed, output, timeline: timeline);
var calibration = new CalibrationSession(feed);
using var commands = new CommandCoordinator();
using var automaticStartup = new AutomaticStartup();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory, WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot") });
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ "; o.UseUtcTimestamp = true; });
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.ConfigureKestrel(o => { o.Limits.MaxRequestBodySize = 65536; o.AddServerHeader = false; });
var port = int.Parse(Option("--port") ?? Environment.GetEnvironmentVariable("F1_HUE_PORT") ?? "8080", System.Globalization.CultureInfo.InvariantCulture);
if (port is < 1 or > 65535) throw new ArgumentException("Port invalide.");
var certificate = Environment.GetEnvironmentVariable("F1_HUE_TLS_CERT");
var scheme = certificate is null ? "http" : "https";
if (certificate is null)
    builder.WebHost.UseUrls($"http://{listen}:{port}");
else
    builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Parse(listen), port, endpoint => endpoint.UseHttps(certificate, Environment.GetEnvironmentVariable("F1_HUE_TLS_PASSWORD"))));
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow; o.SerializerOptions.RespectRequiredConstructorParameters = true; o.SerializerOptions.RespectNullableAnnotations = true; });
var proxyEnabled = ProxyConfiguration.Configure(builder.Services, desktopMode);
var app = builder.Build();
if (proxyEnabled) app.UseForwardedHeaders();
auth.EnsureSetupCode();
if (auth.SetupRequired) Console.WriteLine("Premier lancement : le code de configuration se trouve dans " + auth.SetupFile);
Console.WriteLine($"F1 Hue Sync · {scheme}://{(listen == "0.0.0.0" ? "localhost" : listen)}:{port}" + (simulate ? " · simulation (aucune commande Hue)" : ""));

var buildManifest = Path.Combine(app.Environment.WebRootPath, "build.json");
var uiBuild = File.Exists(buildManifest) ? JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(buildManifest)).GetProperty("id").GetString() : null;
ApiState State() => new(feed.State, runner.State, automaticStartup.State.Initializing, automaticStartup.State,
    output is HueOutput real ? real.RecoveryStatus() : null, calibration.State, store.Read(),
    simulate ? new(true, "simulation", "Pont de démonstration", false) : hue.Status,
    store.Events(limit: 60), store.Sessions(), simulate, DateTimeOffset.UtcNow, version, uiBuild);
var stream = new StateStream(State);
void Changed() => stream.Changed();
feed.Changed += Changed;
runner.Changed += Changed;
calibration.Changed += Changed;
store.Changed += Changed;
automaticStartup.Changed += Changed;
void Idle() { if (runner.State.Running || runner.State.Stopping || runner.State.CleanupPending || output.RecoveryPending) throw new InvalidOperationException("Termine l’arrêt des lampes avant de modifier le pont ou la sélection."); }
async Task StopLights()
{
    if (!runner.State.Running && !runner.State.Stopping && output is HueOutput real && real.RecoveryPending)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            await real.RecoverAsync(deadline.Token);
            runner.RecoveryResolved();
        }
        catch (Exception e) { runner.RecoveryFailed(e); throw; }
    }
    else
        await runner.StopAsync();
}
void Cookie(HttpContext ctx, string token) => ctx.Response.Cookies.Append("f1hue_session", token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = ctx.Request.IsHttps, MaxAge = auth.Desktop ? TimeSpan.FromHours(8) : TimeSpan.FromDays(30), Path = "/", IsEssential = true });
RequestSecurity.Configure(app, auth, commands, desktopMode, port);
// Endpoint filters execute after JSON binding. Auth and Stop do not wait for
// the command gate. Archive downloads happen outside it and use the same epoch.
var api = app.MapGroup("/api");
api.AddEndpointFilter(async (context, next) =>
{
    if (context.HttpContext.Items["command-token"] is not CancellationToken ct)
        return await next(context);
    for (var i = 0; i < context.Arguments.Count; i++)
        if (context.Arguments[i] is CancellationToken)
            context.Arguments[i] = ct;
    if (context.HttpContext.Request.Path == "/api/replay/start")
    {
        ct.ThrowIfCancellationRequested();
        return await next(context);
    }
    return await commands.RunAsync(async _ => await next(context), ct);
});
app.MapGet("/health", () => Results.Ok(new { status = "ok", ready = !automaticStartup.State.Initializing && !runner.State.CleanupPending, version }));
api.MapGet("/auth/status", (HttpContext ctx) => new { mode = auth.Desktop ? "desktop" : "password", setupRequired = auth.SetupRequired, authenticated = auth.Valid(ctx.Request.Cookies["f1hue_session"]) });
if (desktopAccess is not null)
{
    api.MapPost("/auth/desktop/ticket", (HttpContext ctx) => Results.Ok(new { ticket = desktopAccess.CreateTicket(ctx.Request.Headers["X-F1Hue-Launcher"].ToString()) }));
    api.MapPost("/auth/desktop/login", (DesktopLoginRequest body, HttpContext ctx) => { desktopAccess.ConsumeTicket(body.Ticket); Cookie(ctx, auth.CreateSession()); return Results.Ok(new { ok = true }); });
}
else
{
    api.MapPost("/auth/setup", (SetupRequest body, HttpContext ctx) => { auth.Setup(body.Code, body.Password); Cookie(ctx, auth.CreateSession()); return Results.Ok(new { ok = true }); });
    api.MapPost("/auth/login", (LoginRequest body, HttpContext ctx) => { auth.Login(body.Password); Cookie(ctx, auth.CreateSession()); return Results.Ok(new { ok = true }); });
}
api.MapPost("/auth/logout", (HttpContext ctx) => { auth.Logout(ctx.Request.Cookies["f1hue_session"]); ctx.Response.Cookies.Delete("f1hue_session"); return Results.Ok(new { ok = true }); });
api.MapGet("/state", () => State());
api.MapGet("/schema", () => Results.Json(ContractExporter.Schema()));
app.MapGet("/ready", () => Results.Json(new
{
    ready = !automaticStartup.State.Initializing && !runner.State.CleanupPending && automaticStartup.State.Phase != "waiting"
},
    statusCode: automaticStartup.State.Initializing || runner.State.CleanupPending || automaticStartup.State.Phase == "waiting" ? 503 : 200));
api.MapGet("/settings", (HttpContext ctx) => { var settings = store.Read(); ctx.Response.Headers.ETag = "\"" + settings.Revision + "\""; return settings; });
api.MapPatch("/settings", (JsonElement body, HttpContext ctx) => { var settings = SettingsPatch.Apply(store.Read(), body); store.Save(settings, Revision(ctx)); return store.Read(); });
api.MapPost("/live/start", async (CancellationToken ct) => { automaticStartup.Cancel(); await runner.StartAsync("live", ct: ct); return Results.Ok(new { ok = true }); });
api.MapPost("/stop", async () => { automaticStartup.Cancel(); await commands.StopAsync(StopLights); return Results.Ok(new { ok = true }); });
api.MapPost("/test/preview", async (PreviewRequest body, CancellationToken ct) => { if (!Enum.TryParse<RaceFlag>(body.Flag, out var flag) || !Enum.IsDefined(flag) || flag.ToString() != body.Flag) throw new ArgumentException("Drapeau inconnu."); automaticStartup.Cancel(); await runner.StartAsync("preview", flag, ct: ct); return Results.Ok(new { ok = true }); });
api.MapPost("/test/sequence", async (CancellationToken ct) => { automaticStartup.Cancel(); await runner.StartAsync("sequence", ct: ct); return Results.Ok(new { ok = true }); });
api.MapGet("/replay/scenarios", () => ReplayCatalog.Scenarios.Select(s => new Scenario(s.Id, s.Name, s.Description)));
api.MapPost("/replay/start", async (ReplayRequest body, CancellationToken ct) =>
{
    Idle();
    automaticStartup.Cancel();
    var id = body.Id;
    var kind = body.Kind;
    var speed = body.Speed;
    if (!double.IsFinite(speed) || speed < .25 || speed > 100)
        throw new ArgumentException("Vitesse invalide.");
    var events = kind == "archive" ? await ReplayCatalog.ArchiveAsync(id, ct) : kind == "local" ? ReplayCatalog.Local(store, id) : throw new ArgumentException("Type de replay inconnu.");
    return await commands.RunAsync(async token => { Idle(); await runner.StartAsync("replay", replay: events, speed: speed, ct: token); return Results.Ok(new { ok = true, events = events.Length }); }, ct);
});
api.MapGet("/journal", (string? session) => store.Events(session, 1000));
if (simulate) api.MapPost("/simulation/event", (SimulationRequest body) => { if (body.Payload.ValueKind != JsonValueKind.Object) throw new ArgumentException("Le payload doit être un objet."); feed.Simulate(body.Topic, body.Payload); return Results.Ok(new { ok = true }); });
api.MapPost("/calibration/arm", (CalibrationArmRequest body) => { calibration.Arm(body.Mode); return calibration.State; });
api.MapPost("/calibration/seen", () => new { offset = calibration.Seen() });
api.MapPost("/calibration/clock", (CalibrationClockRequest body) => new { offset = calibration.Compare(body.Remaining) });
api.MapPost("/calibration/cancel", () => { calibration.Cancel(); return Results.Ok(new { ok = true }); });
api.MapPost("/calibration/adjust", (OffsetAdjustmentRequest request) =>
{
    var settings = SettingsPatch.AdjustOffset(store.Read(), request.DeltaSeconds);
    store.Save(settings);
    return store.Read();
});
api.MapPost("/hue/discover", async (CancellationToken ct) => new { addresses = simulate ? new[] { "192.168.1.10" } : await HueDiscovery.DiscoverAsync(ct) });
api.MapPost("/hue/pair", async (PairRequest body, CancellationToken ct) => { Idle(); var ip = HueClient.LocalAddress(body.Ip); if (!simulate) await hue.PairAsync(ip, ct); Changed(); return Results.Ok(new { ok = true }); });
api.MapGet("/hue/inventory", async (CancellationToken ct) => simulate ? SimulationOutput.Inventory : await hue.InventoryAsync(ct));
api.MapGet("/hue/diagnostics", async (CancellationToken ct) =>
{
    var selected = store.Read().LightIds.ToHashSet();
    if (simulate)
        return Results.Ok(new
        {
            lights = Array.Empty<object>(),
            scenes = Array.Empty<object>()
        });
    var lights = (await hue.RequestAsync(HttpMethod.Get, "/light", null, ct)).EnumerateArray()
        .Where(l => selected.Contains(l.GetProperty("id").GetString()!)).Select(l => l.Clone()).ToArray();
    var scenes = (await hue.RequestAsync(HttpMethod.Get, "/scene", null, ct)).EnumerateArray()
        .Where(s => s.GetProperty("actions").EnumerateArray().Any(a => selected.Contains(a.GetProperty("target").GetProperty("rid").GetString()!)))
        .Select(s => s.Clone()).ToArray();
    return Results.Ok(new
    {
        lights,
        scenes
    });
});
api.MapPost("/hue/scene", async (SceneRecallRequest request, CancellationToken ct) =>
{
    Idle();
    if (!Guid.TryParse(request.Id, out _))
        throw new ArgumentException("Scène Hue invalide.");
    if (!simulate)
        await hue.RecallSceneAsync(request.Id, store.Read().LightIds, request.Dynamic, ct);
    return Results.Ok(new
    {
        ok = true
    });
});
api.MapPost("/hue/import-selection", async (CancellationToken ct) => { Idle(); var mapped = LegacyImport.ResolveSelection(store, simulate ? SimulationOutput.Inventory : await hue.InventoryAsync(ct)); Changed(); return new { mapped }; });
api.MapPost("/hue/select", async (SelectionRequest request, HttpContext ctx, CancellationToken ct) =>
{
    Idle();
    if (request.LightIds is null || request.GroupIds is null)
        throw new ArgumentException("Sélection invalide.");
    var inventory = simulate ? SimulationOutput.Inventory : await hue.InventoryAsync(ct);
    var selected = HueClient.ResolveSelection(inventory, request.LightIds, request.GroupIds);
    if (request.EntertainmentAreaId is string area && !inventory.Entertainment.Any(a => a.Id == area && a.LightIds.ToHashSet().SetEquals(selected)))
        throw new ArgumentException("La zone Entertainment doit contenir exactement les lampes sélectionnées.");
    store.Save(store.Read() with
    {
        LightIds = selected,
        EntertainmentAreaId = request.EntertainmentAreaId
    }, Revision(ctx));
    store.Delete("legacy_selection");
    Changed();
    return store.Read();
});
api.MapPost("/hue/unpair", () => { Idle(); vault.Clear(); store.Save(store.Read() with { LightIds = [], EntertainmentAreaId = null }); Changed(); return Results.Ok(new { ok = true }); });
api.MapGet("/events", (HttpContext ctx) => stream.ConnectAsync(ctx, auth, app.Lifetime.ApplicationStopping));
api.MapGet("/diagnostics", () => Results.Json(new
{
    version,
    generatedAt = DateTimeOffset.UtcNow,
    feed = new
    {
        feed.State.Connected,
        feed.State.LastDataAt,
        feed.State.SessionStatus,
        feed.State.JournalError,
        feed.State.ProcessingError
    },
    runner = runner.State,
    startup = automaticStartup.State,
    selectedLights = store.Read().LightIds.Length,
    timeline = timeline.Read(),
    databaseVersion = Store.DatabaseVersion
}));
api.MapPost("/backup", () => Results.Ok(new { file = Path.GetFileName(store.Backup()) }));
RecoveryEndpoints.Map(api, output, runner, hue, store, vault, automaticStartup, Changed);
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallback(async context => { if (context.Request.Path.StartsWithSegments("/api")) context.Response.StatusCode = 404; else { context.Response.ContentType = "text/html"; await context.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath, "index.html")); } });

using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(app.Lifetime.ApplicationStopping);
var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var feedTask = args.Contains("--no-feed") || simulate ? Task.CompletedTask : feed.RunAsync(shutdown.Token);
// Recovery and startup use the same command gate as the UI. Stop cancels
// this intention, including attempts waiting for a network connection.
var startup = automaticStartup.RunAsync(async token =>
{
    await listening.Task.WaitAsync(token);
    using var operation = commands.Request(token);
    await commands.RunAsync(async ct =>
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        if (output is HueOutput real)
        {
            try
            {
                await real.RecoverAsync(deadline.Token);
                runner.RecoveryResolved();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { if (real.RecoveryPending) runner.RecoveryFailed(e); throw; }
        }
        if (!simulate && store.Get<LegacySelection>("legacy_selection") is not null && vault.Read() is not null)
            LegacyImport.ResolveSelection(store, await hue.InventoryAsync(deadline.Token));
        if (store.Read().AutoLive)
            await runner.StartAsync("live", ct: deadline.Token);
        return true;
    }, operation.Token);
}, () => store.Read().AutoLive && store.Read().LightIds.Length > 0 && (simulate || vault.Read() is not null), shutdown.Token);
Task stopTask = Task.CompletedTask;
using var onStopping = app.Lifetime.ApplicationStopping.Register(() =>
{
    automaticStartup.Cancel();
    // Begin restoring before Kestrel waits for its open HTTP connections.
    stopTask = commands.StopAsync(StopLights);
});
var stopFile = Path.Combine(store.DirectoryPath, "stop.request");
if (File.Exists(stopFile)) File.Delete(stopFile);
async Task WatchStop()
{
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
    try
    {
        while (await timer.WaitForNextTickAsync(shutdown.Token))
            if (File.Exists(stopFile))
            {
                File.Delete(stopFile);
                app.Lifetime.StopApplication();
                break;
            }
    }
    catch (OperationCanceledException) { }
}
var stopWatcher = WatchStop();
try
{
    await app.StartAsync();
    listening.SetResult();
    await app.WaitForShutdownAsync();
}
finally { shutdown.Cancel(); await feedTask; await startup; await stopWatcher; try { await stopTask; } catch (Exception e) { app.Logger.LogWarning("Arrêt incomplet, récupération conservée : {Message}", e.Message); } }
