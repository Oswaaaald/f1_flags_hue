using System.Text.Json;
using System.Text.Json.Nodes;

namespace F1Hue.Core;

public static class SettingsPatch
{
    public static AppSettings Apply(AppSettings current, JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Object) throw new ArgumentException("Réglages invalides.");
        var target = JsonSerializer.SerializeToNode(current, JsonDefaults.Options)!.AsObject();
        string[] allowed = ["brightness", "transitionSeconds", "offsetSeconds", "alertWatchdogSeconds", "restoreOnExit", "exitOnChequered", "autoLive", "effects"];
        foreach (var prop in patch.EnumerateObject())
        {
            if (!allowed.Contains(prop.Name)) throw new ArgumentException("Réglage inconnu : " + prop.Name);
            if (prop.Name == "effects")
            {
                if (prop.Value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Drapeaux invalides.");
                foreach (var flag in prop.Value.EnumerateObject())
                {
                    if (!Enum.TryParse<RaceFlag>(flag.Name, out var parsed) || !Enum.IsDefined(parsed) || flag.Name != parsed.ToString() || flag.Value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Drapeau inconnu.");
                    foreach (var value in flag.Value.EnumerateObject())
                    {
                        if (value.Name is not ("enabled" or "durationSeconds" or "mode" or "x" or "y")) throw new ArgumentException("Option de drapeau inconnue.");
                        target["effects"]![flag.Name]![value.Name] = JsonNode.Parse(value.Value.GetRawText());
                    }
                }
            }
            else target[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
        }
        return target.Deserialize<AppSettings>(JsonDefaults.Options)!.Validate();
    }
}
