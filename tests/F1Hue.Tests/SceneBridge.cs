using System.Text.Json;
using System.Text.Json.Nodes;

// Bridge resource storage: tests exercise real capture/recall/cleanup requests,
// including distinct per-lamp states and externally edited resources.
internal sealed class SceneBridge
{
    public readonly Dictionary<string, JsonObject> Resources = [];
    public bool RejectRecall, LoseSceneResponse, Offline;
    public static string Device(string light) => "a" + light[1..];
    public string Reply(string path, HttpMethod method, string body, object[] lights)
    {
        var lightNodes = JsonSerializer.SerializeToNode(lights)!.AsArray();
        var all = lightNodes.Select(l => l!.DeepClone()).Concat(lightNodes.Select(l => JsonSerializer.SerializeToNode(new
        {
            id = Device(l!["id"]!.GetValue<string>()), type = "device",
            services = new[] { new { rid = l["id"]!.GetValue<string>(), rtype = "light" } },
        })!)).Concat(Resources.Values.Select(r => r.DeepClone())).ToList();
        if (Offline)
            all.Add(JsonSerializer.SerializeToNode(new { id = Guid.NewGuid().ToString(), type = "zigbee_connectivity",
                owner = new { rid = Device(lightNodes[0]!["id"]!.GetValue<string>()), rtype = "device" }, status = "disconnected" })!);
        const string prefix = "clip/v2/resource";
        var route = path[prefix.Length..];
        if (method == HttpMethod.Get)
        {
            var selected = route == "" ? all : route == "/light" ? lightNodes.Select(l => l!.DeepClone()).ToList()
                : all.Where(r => route == "/" + r["type"]!.GetValue<string>() + "/" + r["id"]!.GetValue<string>()).ToList();
            return JsonSerializer.Serialize(new { errors = Array.Empty<object>(), data = selected });
        }
        if (method == HttpMethod.Post && route is "/zone" or "/scene")
        {
            var resource = JsonNode.Parse(body)!.AsObject();
            if (route == "/zone" && resource["children"]!.AsArray().Any(child => child!["rtype"]!.GetValue<string>() != "light"))
                return "{\"errors\":[{\"description\":\"Zone children must reference light services\"}],\"data\":[]}";
            if (route == "/scene" && resource["actions"]!.AsArray().Any(item =>
            {
                var action = item!["action"]!.AsObject();
                return new[] { "color", "color_temperature", "gradient", "effects" }.Count(action.ContainsKey) > 1;
            })) return "{\"errors\":[{\"description\":\"Scene color modes cannot be combined\"}],\"data\":[]}";
            var id = Guid.NewGuid().ToString(); var type = route[1..];
            resource["id"] = id; resource["type"] = type; Resources[id] = resource;
            if (route == "/scene" && LoseSceneResponse)
            { LoseSceneResponse = false; return "{\"errors\":[{\"description\":\"lost creation reply\"}],\"data\":[]}"; }
            return JsonSerializer.Serialize(new { errors = Array.Empty<object>(), data = new[] { new { rid = id, rtype = type } } });
        }
        if (method == HttpMethod.Put && route.StartsWith("/scene/") && RejectRecall)
            return "{\"errors\":[{\"description\":\"recall unavailable\"}],\"data\":[]}";
        if (method == HttpMethod.Delete) Resources.Remove(route.Split('/')[2]);
        return "{\"errors\":[],\"data\":[]}";
    }
}
