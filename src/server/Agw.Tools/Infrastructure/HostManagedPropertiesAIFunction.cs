using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Agw.Tools.Infrastructure;

internal sealed class HostManagedPropertiesAIFunction : DelegatingAIFunction
{
    public HostManagedPropertiesAIFunction(AIFunction innerFunction, IReadOnlySet<string> hostManagedProperties)
        : base(innerFunction)
    {
        var schema = JsonNode.Parse(innerFunction.JsonSchema.GetRawText())!.AsObject();
        var properties = schema["properties"]!.AsObject();
        foreach (var name in hostManagedProperties)
            properties.Remove(name);

        if (schema["required"] is JsonArray required)
        {
            for (var index = required.Count - 1; index >= 0; index--)
            {
                if (hostManagedProperties.Contains(required[index]!.GetValue<string>()))
                    required.RemoveAt(index);
            }
        }

        JsonSchema = JsonSerializer.SerializeToElement(schema);
    }

    public override JsonElement JsonSchema { get; }
}
