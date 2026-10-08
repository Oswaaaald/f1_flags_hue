using System.Net;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using F1Hue.Core;

namespace F1Hue.Infrastructure;

public sealed record HueLight(string Id, string Name, bool Color, string? LegacyId);
public sealed record HueGroup(string Id, string Name, string Type, string[] LightIds, string? LegacyId);
public sealed record HueArea(string Id, string Name, string[] LightIds, int[] Channels);
public sealed record HueInventory(HueLight[] Lights, HueGroup[] Groups, HueArea[] Entertainment);
public sealed record HueStatus(bool Linked, string? Ip, string? Name, bool Entertainment);
public sealed class HueResourceMissingException() : InvalidOperationException("Une ressource Hue sauvegardée a été supprimée (HTTP 404).");

public sealed class HueClient(IBridgeVault vault, Func<string, string?, Action<string>?, HttpClient>? clientFactory = null, TimeProvider? time = null) : IDisposable
{
    private readonly SemaphoreSlim _groupBudget = new(1, 1);
    private long? _lastGroupWrite;
    internal async Task<IDisposable?> GroupBudgetAsync(bool urgent, CancellationToken ct)
    {
        // Stop bypasses the queue. Ordinary colors and pulse renewals share a
        // budget; they never emulate an animation with repeated REST frames.
        if (urgent)
            return null;
        var clock = time ?? TimeProvider.System;
        await _groupBudget.WaitAsync(ct);
        try
        {
            if (_lastGroupWrite is long last)
            {
                var remaining = TimeSpan.FromSeconds(1) - clock.GetElapsedTime(last);
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining, clock, ct);
            }
            return new GroupBudgetLease(this, clock);
        }
        catch { _groupBudget.Release(); throw; }
    }
    private sealed class GroupBudgetLease(HueClient owner, TimeProvider clock) : IDisposable
    {
        public void Dispose()
        {
            owner._lastGroupWrite = clock.GetTimestamp();
            owner._groupBudget.Release();
        }
    }
    private HttpClient NewClient(string ip, string? pin, Action<string>? capture = null) => (clientFactory ?? Client)(ip, pin, capture);
    private readonly SemaphoreSlim _trust = new(1, 1);
    // Reuse the HTTPS connection, like requests.Session in the Python engine.
    // A changed address or certificate gets a separate pinned transport.
    private readonly ConcurrentDictionary<(string Ip, string Pin), Lazy<HttpClient>> _clients = new();
    public HueStatus Status
    {
        get
        {
            var c = vault.Read();
            return new(c is not null, c?.Ip, c?.Name, c?.ClientKey is not null);
        }
    }
    public static string LocalAddress(string ip)
    {
        if (!IPAddress.TryParse(ip, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("Entre une adresse IPv4 locale, par exemple 192.168.1.20.");
        var b = address.GetAddressBytes();
        if (!(b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 169 && b[1] == 254))
            throw new ArgumentException("L’adresse du pont doit appartenir au réseau local.");
        return address.ToString();
    }
    private static HttpClient Client(string ip, string? pin, Action<string>? firstCertificate = null)
    {
        LocalAddress(ip);
        var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
        {
            if (cert is null)
                return false;
            var actual = Convert.ToHexString(SHA256.HashData(cert.RawData));
            if (pin is not null)
                return CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(actual), System.Text.Encoding.ASCII.GetBytes(pin));
            if (firstCertificate is null)
                return false;
            firstCertificate(actual);
            return true; // TOFU only for /api/config before sending any credentials.
        };
        return new(handler)
        {
            BaseAddress = new Uri($"https://{ip}/"),
            Timeout = TimeSpan.FromSeconds(8)
        };
    }
    private async Task<(string Pin, string Id, string Name)> Identify(string ip, CancellationToken ct)
    {
        string? fingerprint = null;
        using var client = NewClient(ip, null, p => fingerprint = p);
        try
        {
            using var response = await client.GetAsync("api/config", ct);
            response.EnsureSuccessStatusCode();
            var data = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (!data.TryGetProperty("bridgeid", out var id) || fingerprint is null)
                throw new InvalidOperationException("Cette adresse ne répond pas comme un pont Hue.");
            return (fingerprint, id.GetString()!, data.GetProperty("name").GetString() ?? "Hue Bridge");
        }
        catch (HttpRequestException) { throw new InvalidOperationException("Impossible de joindre le pont Hue. Vérifie son adresse et l’autorisation Réseau local de l’application F1 Hue Sync."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new InvalidOperationException("Le pont Hue ne répond pas. Vérifie son alimentation et son réseau."); }
    }
    public async Task PairAsync(string ip, CancellationToken ct, string? expectedBridgeId = null)
    {
        ip = LocalAddress(ip);
        var identity = await Identify(ip, ct);
        if (expectedBridgeId is not null && !string.Equals(expectedBridgeId, identity.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("La récupération appartient à un autre pont. La liaison n’a pas été remplacée.");
        using var client = NewClient(ip, identity.Pin);
        using var response = await client.PostAsJsonAsync("api", new
        {
            devicetype = "f1_hue_sync#local",
            generateclientkey = true
        }, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0)
            throw new InvalidOperationException("Réponse de liaison Hue invalide.");
        var first = result[0];
        if (first.TryGetProperty("error", out var error))
            throw new InvalidOperationException(error.GetProperty("type").GetInt32() == 101 ? "Appuie sur le bouton physique du pont, puis clique sur Lier dans les 30 secondes." : "Le pont a refusé la liaison.");
        var success = first.GetProperty("success");
        vault.Save(new(ip, success.GetProperty("username").GetString()!, success.TryGetProperty("clientkey", out var key) ? key.GetString() : null, identity.Pin, identity.Id, identity.Name));
    }
    public async Task<BridgeCredentials> CredentialsAsync(CancellationToken ct)
    {
        await _trust.WaitAsync(ct);
        try
        {
            var c = vault.Read() ?? throw new InvalidOperationException("Lie d’abord un pont Hue.");
            if (c.CertificatePin is null)
            {
                var id = await Identify(c.Ip, ct);
                c = c with
                {
                    CertificatePin = id.Pin,
                    BridgeId = id.Id,
                    Name = id.Name
                };
                vault.Save(c);
            }
            return c;
        }
        finally { _trust.Release(); }
    }
    public Task<JsonElement> RequestAsync(HttpMethod method, string path, object? body, CancellationToken ct)
        => SendAsync(method, path, body, ct, false);
    public Task<JsonElement> LegacyRequestAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        // In particular, never accept group 0 (all lamps) or arbitrary API paths.
        if (!System.Text.RegularExpressions.Regex.IsMatch(path, @"\A(?:groups(?:/[1-9][0-9]*(?:/action)?)?|lights/[1-9][0-9]*/state)\z"))
            throw new ArgumentException("Cible Hue native invalide.");
        return SendAsync(method, path, body, ct, true);
    }
    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, bool legacy)
    {
        var c = await CredentialsAsync(ct);
        var client = _clients.GetOrAdd((c.Ip, c.CertificatePin!), key => new Lazy<HttpClient>(() => NewClient(key.Ip, key.Pin))).Value;
        using var request = new HttpRequestMessage(method, legacy ? "api/" + Uri.EscapeDataString(c.ApplicationKey) + "/" + path : "clip/v2/resource" + path);
        if (!legacy)
            request.Headers.Add("hue-application-key", c.ApplicationKey);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonDefaults.Options);
        try
        {
            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new InvalidOperationException("Le pont refuse la clé. Lie à nouveau le pont.");
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                    throw new HueResourceMissingException();
                var detail = response.StatusCode switch
                {
                    HttpStatusCode.BadRequest => "Le pont a refusé le contenu de la commande.",
                    HttpStatusCode.NotFound => "La ressource Hue est introuvable. Actualise les lampes dans Hue.",
                    HttpStatusCode.TooManyRequests => "Le pont reçoit trop de commandes. Patiente un instant puis réessaie.",
                    _ => "Le pont a renvoyé une erreur à cette commande."
                };
                var reason = await RejectionReasonAsync(response, c.ApplicationKey, ct);
                throw new InvalidOperationException($"Commande Hue refusée (HTTP {(int)response.StatusCode}). {detail}" + (reason is null ? "" : " " + reason));
            }
            var data = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (legacy)
            {
                if (data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                        if (item.TryGetProperty("error", out var error))
                            throw new InvalidOperationException("Commande Hue native refusée : " + (error.GetProperty("description").GetString() ?? "erreur du pont")
                                .Replace(c.ApplicationKey, "[clé masquée]", StringComparison.Ordinal)
                                .Replace(Uri.EscapeDataString(c.ApplicationKey), "[clé masquée]", StringComparison.Ordinal));
                    if (data.GetArrayLength() == 0 || data.EnumerateArray().Any(item => !item.TryGetProperty("success", out _)))
                        throw new InvalidOperationException("Réponse Hue native invalide.");
                }
                else if (method != HttpMethod.Get || data.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("Réponse Hue native invalide.");
                return data.Clone();
            }
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array
                || !data.TryGetProperty("data", out var resources) || resources.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Réponse Hue invalide.");
            if (errors.GetArrayLength() > 0)
                throw new InvalidOperationException("Commande Hue refusée : " + (errors[0].GetProperty("description").GetString() ?? "erreur du pont").Replace(c.ApplicationKey, "[clé masquée]", StringComparison.Ordinal));
            return resources.Clone();
        }
        catch (HttpRequestException e) when (e.HttpRequestError == HttpRequestError.SecureConnectionError)
        {
            throw new InvalidOperationException("La connexion HTTPS au pont Hue a échoué. Vérifie son certificat et la liaison du pont.");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("Pont Hue inaccessible. Vérifie son adresse, le réseau local et l’autorisation Réseau local de F1 Hue Sync.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new InvalidOperationException("Le pont Hue ne répond pas. Vérifie la connexion au réseau local."); }
        catch (JsonException) { throw new InvalidOperationException("Réponse Hue invalide."); }
    }
    private static async Task<string?> RejectionReasonAsync(HttpResponseMessage response, string key, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("errors", out var errors)
                && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0
                && errors[0].TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String)
            {
                var reason = description.GetString()!.Replace(key, "[clé masquée]", StringComparison.Ordinal)
                    .Replace(Uri.EscapeDataString(key), "[clé masquée]", StringComparison.Ordinal);
                return reason[..Math.Min(reason.Length, 240)];
            }
        }
        catch (JsonException) { }
        return null;
    }
    public Task PutLightAsync(string id, object state, CancellationToken ct) => Guid.TryParse(id, out _) ? RequestAsync(HttpMethod.Put, "/light/" + id, state, ct) : throw new ArgumentException("Lampe invalide.");
    public async Task RecallSceneAsync(string id, string[] selected, bool dynamic, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out _) || selected.Length == 0)
            throw new ArgumentException("Scène ou sélection Hue invalide.");
        var scenes = await RequestAsync(HttpMethod.Get, "/scene/" + id, null, ct);
        if (scenes.GetArrayLength() != 1)
            throw new ArgumentException("Scène Hue introuvable.");
        var scene = scenes[0];
        var targets = scene.GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("target")).ToArray();
        var inventory = await InventoryAsync(ct);
        var group = inventory.Groups.SingleOrDefault(g => g.Id == scene.GetProperty("group").GetProperty("rid").GetString());
        if (group is null || !group.LightIds.ToHashSet().SetEquals(selected)
            || targets.Any(t => t.GetProperty("rtype").GetString() != "light")
            || !targets.Select(t => t.GetProperty("rid").GetString()!).ToHashSet().SetEquals(selected))
            throw new ArgumentException("La scène Hue doit cibler exactement les lampes sélectionnées.");
        await RequestAsync(HttpMethod.Put, "/scene/" + id, new
        {
            recall = new
            {
                action = dynamic ? "dynamic_palette" : "static",
                duration = 400
            }
        }, ct);
    }
    public async Task<HueInventory> InventoryAsync(CancellationToken ct)
    {
        var all = (await RequestAsync(HttpMethod.Get, "", null, ct)).EnumerateArray().ToArray();
        string Name(JsonElement e) => e.TryGetProperty("metadata", out var m) && m.TryGetProperty("name", out var n) ? n.GetString()! : "Sans nom";
        string? Legacy(JsonElement e) => e.TryGetProperty("id_v1", out var value) ? value.GetString() : null;
        var lights = all.Where(e => e.GetProperty("type").GetString() == "light").Select(e => new HueLight(e.GetProperty("id").GetString()!, Name(e), e.TryGetProperty("color", out _), Legacy(e))).ToArray();
        var devices = all.Where(e => e.GetProperty("type").GetString() == "device").ToDictionary(e => e.GetProperty("id").GetString()!, e => e.GetProperty("services").EnumerateArray().Where(s => s.GetProperty("rtype").GetString() == "light").Select(s => s.GetProperty("rid").GetString()!).ToArray());
        var groups = all.Where(e => e.GetProperty("type").GetString() is "room" or "zone").Select(e => new HueGroup(e.GetProperty("id").GetString()!, Name(e), e.GetProperty("type").GetString()!,
            e.GetProperty("children").EnumerateArray().SelectMany(c => c.GetProperty("rtype").GetString() == "light"
                ? new[] { c.GetProperty("rid").GetString()! } : devices.GetValueOrDefault(c.GetProperty("rid").GetString()!, [])).Distinct().ToArray(), Legacy(e))).ToArray();
        // Channels reference entertainment services. Map their owners back to the same light devices.
        var entertainmentDevices = all.Where(e => e.GetProperty("type").GetString() == "entertainment").ToDictionary(e => e.GetProperty("id").GetString()!, e => e.GetProperty("owner").GetProperty("rid").GetString()!);
        var areas = all.Where(e => e.GetProperty("type").GetString() == "entertainment_configuration").Select(e =>
        {
            var channels = e.GetProperty("channels").EnumerateArray().ToArray();
            var areaLights = channels.SelectMany(ch => ch.GetProperty("members").EnumerateArray()).SelectMany(m => devices.GetValueOrDefault(entertainmentDevices.GetValueOrDefault(m.GetProperty("service").GetProperty("rid").GetString()!, ""), [])).Distinct().ToArray();
            return new HueArea(e.GetProperty("id").GetString()!, Name(e), areaLights, channels.Select(ch => ch.GetProperty("channel_id").GetInt32()).ToArray());
        }).ToArray();
        return new(lights, groups, areas);
    }
    public static string[] ResolveSelection(HueInventory inventory, string[] lightIds, string[] groupIds)
    {
        if (groupIds.Any(id => !inventory.Groups.Any(g => g.Id == id)) || lightIds.Any(id => !inventory.Lights.Any(l => l.Id == id)))
            throw new ArgumentException("La sélection contient une lampe ou une zone introuvable.");
        var result = lightIds.Concat(inventory.Groups.Where(g => groupIds.Contains(g.Id)).SelectMany(g => g.LightIds)).Distinct().ToArray();
        if (result.Length == 0)
            throw new ArgumentException("Choisis au moins une lampe.");
        if (result.Any(id => !inventory.Lights.Any(l => l.Id == id && l.Color)))
            throw new ArgumentException("La sélection contient une lampe qui ne prend pas en charge la couleur.");
        return result;
    }
    public void Dispose()
    {
        foreach (var client in _clients.Values)
            if (client.IsValueCreated)
                client.Value.Dispose();
        _trust.Dispose();
    }
}
