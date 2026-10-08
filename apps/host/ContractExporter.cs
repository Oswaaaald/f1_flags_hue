using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Schema;
using F1Hue.Core;
using F1Hue.Infrastructure;

namespace F1Hue.Host;

public static class ContractExporter
{
    private static readonly Dictionary<Type, string> Names = new()
    {
        [typeof(ApiState)] = "State",
        [typeof(AppSettings)] = "Settings",
        [typeof(RaceFlag)] = "Flag",
        [typeof(EffectSpec)] = "Effect",
        [typeof(HueInventory)] = "Inventory",
        [typeof(Scenario)] = "Scenario",
    };
    private static readonly Type[] Roots = [typeof(ApiState), typeof(HueInventory), typeof(Scenario), typeof(ApiError), typeof(SelectionRequest), typeof(SceneRecallRequest), typeof(OffsetAdjustmentRequest), typeof(SetupRequest), typeof(LoginRequest), typeof(DesktopLoginRequest), typeof(PreviewRequest), typeof(ReplayRequest), typeof(PairRequest), typeof(CalibrationArmRequest), typeof(CalibrationClockRequest), typeof(SimulationRequest), typeof(RecoveryAbandonRequest)];
    private static string Name(Type type) => Names.GetValueOrDefault(type, type.Name);
    public static object Schema() => Roots.ToDictionary(Name, type => new JsonSerializerOptions(JsonDefaults.Options) { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() }.GetJsonSchemaAsNode(type));
    public static string TypeScript()
    {
        var queue = new Queue<Type>(Roots);
        var done = new HashSet<Type>();
        var nullable = new NullabilityInfoContext();
        var output = new StringBuilder("// Generated from the .NET API contracts. Run: make contracts\n");
        string TypeName(Type type, NullabilityInfo? info = null)
        {
            if (type == typeof(JsonElement))
                return "unknown";
            if (Nullable.GetUnderlyingType(type) is Type inner)
                return TypeName(inner) + " | null";
            var optional = !type.IsValueType && info?.ReadState == NullabilityState.Nullable ? " | null" : "";
            if (type == typeof(string) || type == typeof(DateTimeOffset) || type == typeof(DateTime) || type == typeof(Guid))
                return "string" + optional;
            if (type == typeof(bool))
                return "boolean";
            if (type.IsPrimitive || type == typeof(decimal))
                return "number";
            if (type.IsArray)
                return "(" + TypeName(type.GetElementType()!, info?.ElementType) + ")[]" + optional;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                return "Record<" + TypeName(type.GenericTypeArguments[0]) + ", " + TypeName(type.GenericTypeArguments[1]) + ">" + optional;
            queue.Enqueue(type);
            return Name(type) + optional;
        }
        while (queue.TryDequeue(out var type))
        {
            if (!done.Add(type))
                continue;
            output.Append("export type ").Append(Name(type)).Append(" = ");
            if (type.IsEnum)
            {
                output.AppendJoin(" | ", Enum.GetNames(type).Select(n => JsonSerializer.Serialize(n))).AppendLine(";");
                continue;
            }
            output.AppendLine("{");
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetMethod is not null))
                output.Append("  ").Append(JsonNamingPolicy.CamelCase.ConvertName(property.Name)).Append(": ").Append(TypeName(property.PropertyType, nullable.Create(property))).AppendLine(";");
            output.AppendLine("};");
        }
        return output.ToString();
    }
}
