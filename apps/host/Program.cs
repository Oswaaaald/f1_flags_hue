using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using F1Hue.Core;
using F1Hue.Host;
using F1Hue.Infrastructure;

var version = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(F1Parser).Assembly)!.InformationalVersion.Split('+')[0];
string? Option(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
if (args.Contains("--health-check"))
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    try { using var response = await http.GetAsync(Environment.GetEnvironmentVariable("F1_HUE_HEALTH_URL") ?? "http://127.0.0.1:" + (Environment.GetEnvironmentVariable("F1_HUE_PORT") ?? "8080") + "/health"); Environment.ExitCode = response.IsSuccessStatusCode ? 0 : 1; }
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
    Console.WriteLine(JsonSerializer.Serialize(new { connected = success, diagnosticFeed.State.SessionName, diagnosticFeed.State.SessionStatus, diagnosticFeed.State.LastDataAt, diagnosticFeed.State.LastError }, JsonDefaults.Options));
    Environment.ExitCode = success ? 0 : 1; return;
}
var desktopMode = args.Contains("--desktop");
var listen = Option("--listen") ?? Environment.GetEnvironmentVariable("F1_HUE_LISTEN") ?? "127.0.0.1";
if (!IPAddress.TryParse(listen, out _)) throw new ArgumentException("--listen attend une adresse IP.");
if (desktopMode && listen != "127.0.0.1") { Console.Error.WriteLine("La connexion automatique de bureau exige --listen 127.0.0.1."); Environment.ExitCode = 2; return; }
var data = Option("--data") ?? Environment.GetEnvironmentVariable("F1_HUE_DATA_DIR") ?? PrivateFiles.DefaultDataPath;
Store ownedStore;
try { ownedStore = new Store(data); }
catch (InvalidOperationException e) { Console.Error.WriteLine(e.Message); Environment.ExitCode = 2; return; }
using var store = ownedStore;
var vault = new SecretVault(store.DirectoryPath);
using var desktopAccess = desktopMode ? new DesktopAccess(store.DirectoryPath) : null;
var auth = new Auth(store, desktopAccess);
if (args.Contains("--reset-password")) { auth.Reset(); Console.WriteLine("Accès réinitialisé. Code de configuration : " + auth.SetupFile); return; }
if (Option("--import") is string legacy) Console.WriteLine(LegacyImport.Import(legacy, store, vault) ? "Configuration existante importée." : "Configuration déjà présente : import ignoré.");
var simulate = args.Contains("--simulate");
var feed = new F1Feed(store);
using var hue = new HueClient(vault);
if (args.Contains("--check-hue"))
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    try
    {
        var inventory = await hue.InventoryAsync(deadline.Token);
        LegacyImport.ResolveSelection(store, inventory);
        Console.WriteLine(JsonSerializer.Serialize(new { connected = true, lights = inventory.Lights.Length, groups = inventory.Groups.Length, selected = store.Read().LightIds.Length, entertainmentAreas = inventory.Entertainment.Length }, JsonDefaults.Options));
    }
    catch (Exception e) { Console.WriteLine("Vérification Hue : " + e.Message); Environment.ExitCode = 1; }
    return;
}
IEffectOutput output = simulate ? new SimulationOutput() : new HueOutput(hue, store);
await using var runner = new Runner(store, feed, output);
var calibration = new CalibrationSession(feed);
using var commands = new CommandCoordinator();
var initializing = true;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory, WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot") });
builder.Logging.ClearProviders(); builder.Logging.AddSimpleConsole(o => o.SingleLine = true); builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.ConfigureKestrel(o => { o.Limits.MaxRequestBodySize = 65536; o.AddServerHeader = false; });
var port = int.Parse(Option("--port") ?? Environment.GetEnvironmentVariable("F1_HUE_PORT") ?? "8080", System.Globalization.CultureInfo.InvariantCulture);
if (port is < 1 or > 65535) throw new ArgumentException("Port invalide.");
var certificate = Environment.GetEnvironmentVariable("F1_HUE_TLS_CERT");
var scheme = certificate is null ? "http" : "https";
if (certificate is null) builder.WebHost.UseUrls($"http://{listen}:{port}");
else builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Parse(listen), port, endpoint => endpoint.UseHttps(certificate, Environment.GetEnvironmentVariable("F1_HUE_TLS_PASSWORD"))));
builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow; });
var app = builder.Build();
auth.EnsureSetupCode();
if (auth.SetupRequired) Console.WriteLine("Premier lancement : le code de configuration se trouve dans " + auth.SetupFile);
Console.WriteLine($"F1 Hue Sync · {scheme}://{(listen == "0.0.0.0" ? "localhost" : listen)}:{port}" + (simulate ? " · simulation (aucune commande Hue)" : ""));

var subscribers = new System.Collections.Concurrent.ConcurrentDictionary<Guid, Channel<bool>>();
void Changed() { foreach (var channel in subscribers.Values) channel.Writer.TryWrite(true); }
feed.Changed += Changed; runner.Changed += Changed; calibration.Changed += Changed;
object State() => new { feed = feed.State, runner = runner.State, initializing, calibration = calibration.State, settings = store.Read(), hue = simulate ? (object)new { linked = true, ip = "simulation", name = "Pont de démonstration", entertainment = false } : hue.Status, journal = store.Events(limit: 60), sessions = store.Sessions(), simulation = simulate, serverUtc = DateTimeOffset.UtcNow, version };
void Idle() { if (runner.State.Running || runner.State.CleanupPending) throw new InvalidOperationException("Termine l’arrêt des lampes avant de modifier le pont ou la sélection."); }
string Text(JsonElement body, string name) => body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new ArgumentException("Champ requis : " + name);
void Cookie(HttpContext ctx, string token) => ctx.Response.Cookies.Append("f1hue_session", token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = ctx.Request.IsHttps, MaxAge = auth.Desktop ? TimeSpan.FromHours(8) : TimeSpan.FromDays(30), Path = "/", IsEssential = true });
var configuredHosts = (Environment.GetEnvironmentVariable("F1_HUE_ALLOWED_HOSTS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.Headers.CacheControl = "no-store";
    else if (!Path.HasExtension(ctx.Request.Path) || ctx.Request.Path.Value?.EndsWith(".html", StringComparison.OrdinalIgnoreCase) == true)
        ctx.Response.Headers.CacheControl = "no-cache";
    var host = ctx.Request.Host.Host;
    if (desktopMode && (host != "127.0.0.1" || ctx.Request.Host.Port != port || ctx.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote))) { ctx.Response.StatusCode = 403; return; }
    if (!(host == "localhost" || IPAddress.TryParse(host, out _) || configuredHosts.Contains(host, StringComparer.OrdinalIgnoreCase))) { ctx.Response.StatusCode = 403; return; }
    var origin = ctx.Request.Headers.Origin.ToString();
    if (ctx.Request.Headers["Sec-Fetch-Site"] == "cross-site" || origin.Length > 0 && origin != $"{ctx.Request.Scheme}://{ctx.Request.Host}") { ctx.Response.StatusCode = 403; return; }
    var unsafeMethod = !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method);
    if (unsafeMethod && ctx.Request.Headers["X-F1Hue-Request"] != "1") { ctx.Response.StatusCode = 403; return; }
    if (ctx.Request.Path.StartsWithSegments("/api") && !ctx.Request.Path.StartsWithSegments("/api/auth") && !auth.Valid(ctx.Request.Cookies["f1hue_session"])) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsJsonAsync(new { error = "Connecte-toi pour accéder à F1 Hue Sync." }); return; }
    // Capture cancellation before binding a potentially slow body, without taking
    // the control lock. A request received before Stop must not start afterwards.
    using var operation = unsafeMethod && !ctx.Request.Path.StartsWithSegments("/api/auth") && ctx.Request.Path != "/api/stop"
        ? commands.Request(ctx.RequestAborted) : null;
    if (operation is not null) ctx.Items["command-token"] = operation.Token;
    try
    {
        if (unsafeMethod && ctx.Request.Path is var authPath && (authPath == "/api/auth/login" || authPath == "/api/auth/setup"))
            auth.Limit(ctx.Connection.RemoteIpAddress?.ToString() ?? "local");
        await next(ctx);
    }
    catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
    catch (OperationCanceledException) when (operation?.IsCancellationRequested == true && !ctx.Response.HasStarted)
    { ctx.Response.StatusCode = 409; await ctx.Response.WriteAsJsonAsync(new { error = "Opération annulée par Stop." }); }
    catch (Exception e) when (!ctx.Response.HasStarted)
    {
        ctx.Response.StatusCode = e switch { UnauthorizedAccessException => 401, ArgumentException or JsonException or BadHttpRequestException => 400, InvalidOperationException => 409, HttpRequestException or OperationCanceledException => 502, _ => 500 };
        await ctx.Response.WriteAsJsonAsync(new { error = ctx.Response.StatusCode == 500 ? "Une erreur interne est survenue. Consulte les journaux du service." : e.Message });
        if (ctx.Response.StatusCode == 500) app.Logger.LogError("Erreur interne : {Type}", e.GetType().Name);
    }
});
// Endpoint filters execute after JSON binding. Auth and Stop do not wait for
// the command gate. Archive downloads happen outside it and use the same epoch.
var api = app.MapGroup("/api");
api.AddEndpointFilter(async (context, next) =>
{
    if (context.HttpContext.Items["command-token"] is not CancellationToken ct) return await next(context);
    for (var i = 0; i < context.Arguments.Count; i++)
        if (context.Arguments[i] is CancellationToken) context.Arguments[i] = ct;
    if (context.HttpContext.Request.Path == "/api/replay/start") { ct.ThrowIfCancellationRequested(); return await next(context); }
    return await commands.RunAsync(async _ => await next(context), ct);
});
app.MapGet("/health", () => Results.Ok(new { status = "ok", ready = !initializing, version }));
api.MapGet("/auth/status", (HttpContext ctx) => new { mode = auth.Desktop ? "desktop" : "password", setupRequired = auth.SetupRequired, authenticated = auth.Valid(ctx.Request.Cookies["f1hue_session"]) });
if (desktopAccess is not null)
{
    api.MapPost("/auth/desktop/ticket", (HttpContext ctx) => Results.Ok(new { ticket = desktopAccess.CreateTicket(ctx.Request.Headers["X-F1Hue-Launcher"].ToString()) }));
    api.MapPost("/auth/desktop/login", (JsonElement body, HttpContext ctx) => { desktopAccess.ConsumeTicket(Text(body, "ticket")); Cookie(ctx, auth.CreateSession()); return Results.Ok(new { ok = true }); });
}
else
{
    api.MapPost("/auth/setup", (JsonElement body, HttpContext ctx) => { auth.Setup(Text(body, "code"), Text(body, "password")); Cookie(ctx, auth.CreateSession()); return Results.Ok(new { ok = true }); });
    api.MapPost("/auth/login", (JsonElement body, HttpContext ctx) => { auth.Login(Text(body, "password")); Cookie(ctx, auth.CreateSession()); return Results.Ok(new { ok = true }); });
}
api.MapPost("/auth/logout", (HttpContext ctx) => { auth.Logout(ctx.Request.Cookies["f1hue_session"]); ctx.Response.Cookies.Delete("f1hue_session"); return Results.Ok(new { ok = true }); });
api.MapGet("/state", () => State());
api.MapGet("/settings", () => store.Read());
api.MapPatch("/settings", (JsonElement body) => { var settings = SettingsPatch.Apply(store.Read(), body); store.Save(settings); Changed(); return settings; });
api.MapPost("/live/start", async (CancellationToken ct) => { await runner.StartAsync("live", ct: ct); return Results.Ok(new { ok = true }); });
api.MapPost("/stop", async () => { await commands.StopAsync(runner.StopAsync); return Results.Ok(new { ok = true }); });
api.MapPost("/test/preview", async (JsonElement body, CancellationToken ct) => { if (!Enum.TryParse<RaceFlag>(Text(body, "flag"), out var flag) || !Enum.IsDefined(flag)) throw new ArgumentException("Drapeau inconnu."); await runner.StartAsync("preview", flag, ct: ct); return Results.Ok(new { ok = true }); });
api.MapPost("/test/sequence", async (CancellationToken ct) => { await runner.StartAsync("sequence", ct: ct); return Results.Ok(new { ok = true }); });
api.MapGet("/replay/scenarios", () => ReplayCatalog.Scenarios.Select(s => new { s.Id, s.Name, s.Description }));
api.MapPost("/replay/start", async (JsonElement body, CancellationToken ct) =>
{
    Idle(); var id = Text(body, "id"); var kind = Text(body, "kind");
    var speed = body.TryGetProperty("speed", out var v) && v.TryGetDouble(out var number) ? number : 1;
    if (!double.IsFinite(speed) || speed < .25 || speed > 100) throw new ArgumentException("Vitesse invalide.");
    var events = kind == "archive" ? await ReplayCatalog.ArchiveAsync(id, ct) : kind == "local" ? ReplayCatalog.Local(store, id) : throw new ArgumentException("Type de replay inconnu.");
    return await commands.RunAsync(async token => { Idle(); await runner.StartAsync("replay", replay: events, speed: speed, ct: token); return Results.Ok(new { ok = true, events = events.Length }); }, ct);
});
api.MapGet("/journal", (string? session) => store.Events(session, 1000));
if (simulate) api.MapPost("/simulation/event", (JsonElement body) => { feed.Simulate(Text(body, "topic"), body.GetProperty("payload")); return Results.Ok(new { ok = true }); });
api.MapPost("/calibration/arm", (JsonElement body) => { calibration.Arm(Text(body, "mode")); return calibration.State; });
api.MapPost("/calibration/seen", () => new { offset = calibration.Seen() });
api.MapPost("/calibration/clock", (JsonElement body) => new { offset = calibration.Compare(Text(body, "remaining")) });
api.MapPost("/calibration/cancel", () => { calibration.Cancel(); return Results.Ok(new { ok = true }); });
api.MapPost("/hue/discover", async (CancellationToken ct) => new { addresses = simulate ? new[] { "192.168.1.10" } : await HueDiscovery.DiscoverAsync(ct) });
api.MapPost("/hue/pair", async (JsonElement body, CancellationToken ct) => { Idle(); if (!simulate) await hue.PairAsync(Text(body, "ip"), ct); Changed(); return Results.Ok(new { ok = true }); });
api.MapGet("/hue/inventory", async (CancellationToken ct) => simulate ? SimulationOutput.Inventory : await hue.InventoryAsync(ct));
api.MapGet("/hue/diagnostics", async (CancellationToken ct) =>
{
    var selected = store.Read().LightIds.ToHashSet();
    if (simulate) return Results.Ok(new { lights = Array.Empty<object>(), scenes = Array.Empty<object>() });
    var lights = (await hue.RequestAsync(HttpMethod.Get, "/light", null, ct)).EnumerateArray()
        .Where(l => selected.Contains(l.GetProperty("id").GetString()!)).Select(l => l.Clone()).ToArray();
    var scenes = (await hue.RequestAsync(HttpMethod.Get, "/scene", null, ct)).EnumerateArray()
        .Where(s => s.GetProperty("actions").EnumerateArray().Any(a => selected.Contains(a.GetProperty("target").GetProperty("rid").GetString()!)))
        .Select(s => s.Clone()).ToArray();
    return Results.Ok(new { lights, scenes });
});
api.MapPost("/hue/scene", async (SceneRecallRequest request, CancellationToken ct) =>
{
    Idle();
    if (!Guid.TryParse(request.Id, out _)) throw new ArgumentException("Scène Hue invalide.");
    if (!simulate) await hue.RecallSceneAsync(request.Id, store.Read().LightIds, request.Dynamic, ct);
    return Results.Ok(new { ok = true });
});
api.MapPost("/hue/import-selection", async (CancellationToken ct) => { Idle(); var mapped = LegacyImport.ResolveSelection(store, simulate ? SimulationOutput.Inventory : await hue.InventoryAsync(ct)); Changed(); return new { mapped }; });
api.MapPost("/hue/select", async (SelectionRequest request, CancellationToken ct) =>
{
    Idle(); if (request.LightIds is null || request.GroupIds is null) throw new ArgumentException("Sélection invalide.");
    var inventory = simulate ? SimulationOutput.Inventory : await hue.InventoryAsync(ct);
    var selected = HueClient.ResolveSelection(inventory, request.LightIds, request.GroupIds);
    if (request.EntertainmentAreaId is string area && !inventory.Entertainment.Any(a => a.Id == area && a.LightIds.ToHashSet().SetEquals(selected))) throw new ArgumentException("La zone Entertainment doit contenir exactement les lampes sélectionnées.");
    store.Save(store.Read() with { LightIds = selected, EntertainmentAreaId = request.EntertainmentAreaId }); store.Delete("legacy_selection"); Changed(); return store.Read();
});
api.MapPost("/hue/unpair", () => { Idle(); vault.Clear(); store.Save(store.Read() with { LightIds = [], EntertainmentAreaId = null }); Changed(); return Results.Ok(new { ok = true }); });
api.MapGet("/events", async (HttpContext ctx) =>
{
    if (subscribers.Count >= 20) throw new InvalidOperationException("Trop de pages connectées. Ferme un autre onglet.");
    var id = Guid.NewGuid(); var queue = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest }); subscribers[id] = queue; queue.Writer.TryWrite(true);
    ctx.Response.ContentType = "text/event-stream"; ctx.Response.Headers["X-Accel-Buffering"] = "no";
    try
    {
        while (!ctx.RequestAborted.IsCancellationRequested && auth.Valid(ctx.Request.Cookies["f1hue_session"]))
        {
            using var tick = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted); tick.CancelAfter(TimeSpan.FromSeconds(15));
            try { await queue.Reader.ReadAsync(tick.Token); } catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested) { }
            await ctx.Response.WriteAsync("data: " + JsonSerializer.Serialize(State(), JsonDefaults.Options) + "\n\n", ctx.RequestAborted); await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }
    }
    catch (OperationCanceledException) { }
    finally { subscribers.TryRemove(id, out _); }
});
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapFallback(async context => { if (context.Request.Path.StartsWithSegments("/api")) context.Response.StatusCode = 404; else { context.Response.ContentType = "text/html"; await context.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath, "index.html")); } });

using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(app.Lifetime.ApplicationStopping);
var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var feedTask = args.Contains("--no-feed") || simulate ? Task.CompletedTask : feed.RunAsync(shutdown.Token);
// Recovery is limited to a persisted, explicit lamp baseline, after an interrupted previous run.
async Task Startup()
{
    using var operation = commands.Request(shutdown.Token);
    try
    {
        await commands.RunAsync(async ct =>
        {
        await listening.Task.WaitAsync(ct);
        using var recoveryDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        recoveryDeadline.CancelAfter(TimeSpan.FromSeconds(25));
        if (output is HueOutput real)
            try
            {
                var pendingRecovery = store.Get<JsonElement?>("pending_restore") is not null || store.Get<string>("pending_entertainment") is not null || store.Get<string[]>("pending_native_alert") is not null
                    || store.Get<JsonElement?>("hue_snapshot") is not null;
                await real.RecoverAsync(recoveryDeadline.Token);
                if (pendingRecovery) Console.WriteLine("Hue : récupération des lampes terminée après l’arrêt incomplet.");
            }
            catch (Exception e) { runner.RecoveryFailed(e); throw; }
        if (!simulate && store.Get<LegacySelection>("legacy_selection") is not null && vault.Read() is not null)
            LegacyImport.ResolveSelection(store, await hue.InventoryAsync(ct));
        if (store.Read().AutoLive) await runner.StartAsync("live", ct: ct);
        return true;
        }, operation.Token);
    }
    catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
    catch (Exception e) { app.Logger.LogWarning("Démarrage automatique : {Message}", e.Message); }
    finally { initializing = false; Changed(); }
}
var startup = Startup();
var stopFile = Path.Combine(store.DirectoryPath, "stop.request");
if (File.Exists(stopFile)) File.Delete(stopFile);
async Task WatchStop()
{
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
    try { while (await timer.WaitForNextTickAsync(shutdown.Token)) if (File.Exists(stopFile)) { File.Delete(stopFile); app.Lifetime.StopApplication(); break; } }
    catch (OperationCanceledException) { }
}
var stopWatcher = WatchStop();
try { await app.StartAsync(); listening.SetResult(); await app.WaitForShutdownAsync(); }
finally { shutdown.Cancel(); await feedTask; await startup; await stopWatcher; await runner.StopAsync(); }

public sealed record SelectionRequest(string[] LightIds, string[] GroupIds, string? EntertainmentAreaId);
public sealed record SceneRecallRequest(string Id, bool Dynamic = false);
public sealed class SimulationOutput : IEffectOutput
{
    public static readonly HueInventory Inventory = new([new("11111111-1111-1111-1111-111111111111", "Lampe salon", true, "/lights/1"), new("22222222-2222-2222-2222-222222222222", "Ruban TV", true, "/lights/2")], [new("33333333-3333-3333-3333-333333333333", "Salon", "room", ["11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222"], "/groups/1")], []);
    public Task CaptureAsync(AppSettings settings, CancellationToken ct) => Task.CompletedTask;
    public Task ApplyAsync(RaceFlag flag, EffectSpec effect, AppSettings settings, CancellationToken ct) => Task.CompletedTask;
    public Task EndAnimationAsync(CancellationToken ct) => Task.CompletedTask;
    public Task RestoreAsync(CancellationToken ct) => Task.CompletedTask;
}
