using System.Globalization;
using F1Hue.Core;
using YamlDotNet.Serialization;

namespace F1Hue.Infrastructure;

public sealed record LegacySelection(string[] Lights, string[] Groups);
public sealed record ImportedConfig(AppSettings Settings, BridgeCredentials? Bridge, LegacySelection Selection);
public static class LegacyImport
{
    public static ImportedConfig Parse(string yaml)
    {
        var root = new DeserializerBuilder().Build().Deserialize<Dictionary<object, object?>>(yaml) ?? [];
        static object? Get(Dictionary<object, object?> d, string key) => d.GetValueOrDefault(key);
        static Dictionary<object, object?> Map(object? o) => o as Dictionary<object, object?> ?? [];
        static double Num(object? o, double fallback) => o is null ? fallback : double.Parse(Convert.ToString(o, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
        static bool Bool(object? o, bool fallback) => o is null ? fallback : bool.Parse(o.ToString()!);
        static string[] Strings(object? o) => o is IEnumerable<object> list ? list.Select(v => v.ToString()!).ToArray() : [];
        var flags = Map(Get(root, "flags")); var baseline = Map(Get(root, "baseline")); var behavior = Map(Get(root, "behavior"));
        var patterns = Map(Get(root, "patterns")); var enabled = Map(Get(flags, "enabled")); var colors = Map(Get(root, "colors_xy"));
        var mapping = Map(Get(Map(Get(root, "rules")), "flag_patterns"));
        var effects = AppSettings.Defaults();
        foreach (var flag in Enum.GetValues<RaceFlag>())
        {
            var key = flag.ToString(); var patternName = mapping.TryGetValue(key, out var mapped) ? mapped?.ToString() : key;
            var p = Map(Get(patterns, patternName ?? key)); var old = effects[flag];
            var on = enabled.TryGetValue(key, out var explicitEnabled) ? Bool(explicitEnabled, old.Enabled) : patternName is not null && (flag != RaceFlag.BLUE || !Bool(Get(flags, "ignore_blue"), true));
            var mode = Get(p, "mode")?.ToString() ?? old.Mode; double? duration;
            if (p.TryGetValue("duration_seconds", out var d)) duration = d is null ? null : Num(d, 0);
            else if (mode == "solid") duration = Bool(Get(p, "then_off"), false) ? Num(Get(p, "hold"), 0) : null;
            else if (Get(p, "alert_mode")?.ToString() == "select") duration = Num(Get(p, "select_repeats"), 1) * Num(Get(p, "select_gap"), .6);
            else { var seconds = Num(Get(p, "duration"), 0); duration = seconds > 0 ? seconds : null; }
            var xy = Get(colors, Get(p, "color")?.ToString() ?? "") as List<object>;
            effects[flag] = new(on, p.Count == 0 ? old.DurationSeconds : duration, mode, xy is { Count: 2 } ? Num(xy[0], old.X) : old.X, xy is { Count: 2 } ? Num(xy[1], old.Y) : old.Y);
        }
        var settings = new AppSettings
        {
            Brightness = (int)Num(Get(root, "bri"), 254), TransitionSeconds = Num(Get(root, "transition_tenths"), 5) / 10,
            OffsetSeconds = Num(Get(Map(Get(root, "sync")), "offset_seconds"), Num(Get(root, "offset_seconds"), 0)),
            RestoreOnExit = Bool(Get(baseline, "restore_on_exit"), true), AlertWatchdogSeconds = Num(Get(behavior, "alert_watchdog_seconds"), 600),
            ExitOnChequered = Bool(Get(flags, "exit_on_chequered"), false), Effects = effects,
        };
        var ip = Get(root, "bridge_ip")?.ToString(); var username = Get(root, "username")?.ToString();
        var bridge = string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(username) ? null : new BridgeCredentials(HueClient.LocalAddress(ip), username);
        var groups = Strings(Get(root, "group_ids"));
        if (groups.Length == 0 && Get(root, "group_id") is object group) groups = [group.ToString()!];
        return new(settings.Validate(), bridge, new(Strings(Get(root, "light_ids")), groups));
    }
    public static bool Import(string path, Store store, SecretVault vault)
    {
        if (store.Get<AppSettings>("settings") is not null) return false;
        if (new FileInfo(path).Length > 1_000_000) throw new ArgumentException("Configuration trop volumineuse.");
        var imported = Parse(File.ReadAllText(path));
        if (imported.Bridge is not null) vault.Save(imported.Bridge);
        store.Put("legacy_selection", imported.Selection); store.Save(imported.Settings);
        store.Put("legacy_import", new { at = DateTimeOffset.UtcNow, source = Path.GetFileName(path) });
        return true;
    }
    public static bool ResolveSelection(Store store, HueInventory inventory)
    {
        var pending = store.Get<LegacySelection>("legacy_selection"); if (pending is null) return false;
        if (pending.Groups.Contains("0") || pending.Lights.Length + pending.Groups.Length == 0) return false; // Old 'all lights' requires an explicit choice.
        var lights = pending.Lights.Select(id => inventory.Lights.FirstOrDefault(l => l.LegacyId == "/lights/" + id)?.Id).ToArray();
        var groups = pending.Groups.Select(id => inventory.Groups.FirstOrDefault(g => g.LegacyId == "/groups/" + id)?.Id).ToArray();
        if (lights.Any(id => id is null) || groups.Any(id => id is null)) return false;
        var resolved = HueClient.ResolveSelection(inventory, lights.Select(id => id!).ToArray(), groups.Select(id => id!).ToArray());
        store.Save(store.Read() with { LightIds = resolved }); store.Delete("legacy_selection"); return true;
    }
}
