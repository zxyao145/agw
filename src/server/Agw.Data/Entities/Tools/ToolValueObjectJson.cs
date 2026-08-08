using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agw.Shared.Data.Entities.Tools;

public static class ToolValueObjectJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        AllowOutOfOrderMetadataProperties = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static string Serialize(IReadOnlyList<ToolValueObject> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return JsonSerializer.Serialize(values, SerializerOptions);
    }

    public static List<ToolValueObject> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using var document = JsonDocument.Parse(json);
        var elements = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToArray()
            : [];
        if (elements.Length == 0)
        {
            return JsonSerializer.Deserialize<List<ToolValueObject>>(json, SerializerOptions) ?? [];
        }

        if (elements.All(static element => element.ValueKind == JsonValueKind.String))
        {
            return elements.Select(CreateLegacyToolValue).ToList();
        }

        var values = JsonSerializer.Deserialize<List<ToolValueObject>>(json, SerializerOptions) ?? [];
        return values;
    }

    public static List<ToolValueObject> Clone(IReadOnlyList<ToolValueObject>? values) =>
        values == null ? [] : Deserialize(Serialize(values));

    public static bool SequenceEqual(
        IReadOnlyList<ToolValueObject>? left,
        IReadOnlyList<ToolValueObject>? right) =>
        ReferenceEquals(left, right) ||
        left != null && right != null &&
        string.Equals(Serialize(left), Serialize(right), StringComparison.Ordinal);

    public static int GetSequenceHashCode(IReadOnlyList<ToolValueObject>? values) =>
        values == null ? 0 : StringComparer.Ordinal.GetHashCode(Serialize(values));

    private static ToolValueObject CreateLegacyToolValue(JsonElement element)
    {
        var name = element.GetString();
        var canonicalName = ToolDefinitionNames.All.FirstOrDefault(
            candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)) ?? name;
        var definitionJson = JsonSerializer.Serialize(
            new
            {
                name = canonicalName,
                options = new { }
            },
            SerializerOptions);

        var definition = JsonSerializer.Deserialize<ToolDefinition>(
            definitionJson,
            SerializerOptions);
        return new ToolValue
        {
            Definition = definition!
        };
    }
}
