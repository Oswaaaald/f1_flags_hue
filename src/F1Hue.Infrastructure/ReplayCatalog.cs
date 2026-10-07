using System.Globalization;
using System.Text;
using System.Text.Json;
using F1Hue.Core;

namespace F1Hue.Infrastructure;

public sealed record ArchiveScenario(string Id, string Name, string Description, string Path);
public static class ReplayCatalog
{
    public static readonly ArchiveScenario[] Scenarios =
    [
        new("baku-qualifying-2025", "Bakou 2025 · Qualifications", "Drapeaux rouges, jaunes et damier", "2025/2025-09-21_Azerbaijan_Grand_Prix/2025-09-20_Qualifying/"),
        new("silverstone-race-2025", "Silverstone 2025 · Course", "Safety cars, VSC et arrivée", "2025/2025-07-06_British_Grand_Prix/2025-07-06_Race/"),
        new("monza-practice-2025", "Monza 2025 · Essais libres 3", "Drapeaux jaunes et fin de séance", "2025/2025-09-07_Italian_Grand_Prix/2025-09-06_Practice_3/"),
    ];
    public static ReplayItem[] Local(Store store, string key)
    {
        var events = store.Events(key, 100000).Where(e => e.Kind == "flag").OrderBy(e => e.ReceivedAt).ToArray();
        if (events.Length == 0) throw new ArgumentException("Cette séance n’a pas de drapeaux enregistrés.");
        return events.Select(e => new ReplayItem((e.ReceivedAt - events[0].ReceivedAt).TotalSeconds, Enum.Parse<RaceFlag>(e.Value!))).ToArray();
    }
    public static async Task<string> DownloadAsync(HttpClient http, string url, CancellationToken ct, TimeSpan? timeout = null)
    {
        // HttpClient.Timeout stops at response headers with ResponseHeadersRead.
        // This deadline also covers every read of a slow or stalled response body.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));
        ct = deadline.Token;
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 2_000_000) throw new InvalidOperationException("Archive F1 trop volumineuse.");
        using var stream = await response.Content.ReadAsStreamAsync(ct); using var memory = new MemoryStream(); var buffer = new byte[8192];
        while (true) { var count = await stream.ReadAsync(buffer, ct); if (count == 0) break; if (memory.Length + count > 2_000_000) throw new InvalidOperationException("Archive F1 trop volumineuse."); memory.Write(buffer, 0, count); }
        return Encoding.UTF8.GetString(memory.ToArray()).TrimStart('\uFEFF');
    }
    public static async Task<ReplayItem[]> ArchiveAsync(string id, CancellationToken ct)
    {
        var scenario = Scenarios.FirstOrDefault(s => s.Id == id) ?? throw new ArgumentException("Scénario inconnu.");
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
        var root = "https://livetiming.formula1.com/static/" + scenario.Path;
        var files = await Task.WhenAll(DownloadAsync(http, root + "SessionStatus.jsonStream", ct), DownloadAsync(http, root + "RaceControlMessages.jsonStream", ct), DownloadAsync(http, root + "TrackStatus.jsonStream", ct));
        return Parse(files[0], files[1], files[2]);
    }
    public static ReplayItem[] Parse(string statuses, string messages, string tracks = "")
    {
        static IEnumerable<(double At, string Topic, JsonElement Data)> Rows(string source, string topic)
        {
            foreach (var line in source.Split('\n'))
            {
                var start = line.IndexOf('{'); if (start < 0) continue;
                if (!TimeSpan.TryParseExact(line[..start].Trim(), @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out var at)) continue;
                yield return (at.TotalSeconds, topic, JsonSerializer.Deserialize<JsonElement>(line[start..]));
            }
        }
        var parser = new F1Parser(); var result = new List<ReplayItem>();
        var rows = Rows(statuses, "SessionStatus").Concat(Rows(tracks, "TrackStatus")).Concat(Rows(messages, "RaceControlMessages")).OrderBy(r => r.At).ToArray();
        foreach (var row in rows)
            foreach (var item in parser.Update(row.Topic, row.Data))
                if (item.Kind == "flag") result.Add(new(row.At, Enum.Parse<RaceFlag>(item.Value!)));
        if (result.Count == 0) throw new InvalidOperationException("Aucun drapeau dans cette archive F1.");
        var first = result[0].AtSeconds;
        return result.Select(e => e with { AtSeconds = e.AtSeconds - first }).ToArray();
    }
}
