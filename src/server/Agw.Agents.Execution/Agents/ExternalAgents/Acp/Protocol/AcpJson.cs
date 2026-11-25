using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agw.Agents.Execution.Agents.ExternalAgents.Acp.Protocol;

internal static class AcpJson
{
    public static JsonSerializerOptions Options { get; } =
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            AllowOutOfOrderMetadataProperties = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
}

// 缺省字段保留 C# null，显式 JSON null 保留 JsonValueKind.Null。
// Keep absent fields as C# null and explicit JSON null as JsonValueKind.Null.
internal sealed class AcpRawValueConverter : JsonConverter<JsonElement?>
{
    public override bool HandleNull => true;

    public override JsonElement? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonElement.ParseValue(ref reader);

    public override void Write(Utf8JsonWriter writer, JsonElement? value, JsonSerializerOptions options)
    {
        if (value is { } element)
            element.WriteTo(writer);
        else
            writer.WriteNullValue();
    }
}
