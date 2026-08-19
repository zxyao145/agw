using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Agw.Domain.Tools;

internal static class AgwAIFunctionFactory
{
    public static AITool CreateParameterObjectFunction<TParams, TResult>(Func<TParams, TResult> function, string name)
    {
        var innerFunction = AIFunctionFactory.Create(function, name);
        return new FlattenedParameterObjectAIFunction(innerFunction);
    }

    public static AITool CreateParameterObjectFunction<TParams, TResult>(
        Func<TParams, CancellationToken, Task<TResult>> function,
        string name
    )
    {
        var innerFunction = AIFunctionFactory.Create(function, name);
        return new FlattenedParameterObjectAIFunction(innerFunction);
    }

    private sealed class FlattenedParameterObjectAIFunction : DelegatingAIFunction
    {
        private readonly string _parameterName;
        private readonly JsonElement _jsonSchema;

        public FlattenedParameterObjectAIFunction(AIFunction innerFunction)
            : base(innerFunction)
        {
            _parameterName = DetectParameterName(innerFunction.JsonSchema);
            _jsonSchema = FlattenSchema(innerFunction.JsonSchema, _parameterName);
        }

        public override JsonElement JsonSchema => _jsonSchema;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken
        )
        {
            if (arguments.ContainsKey(_parameterName))
            {
                return base.InvokeCoreAsync(arguments, cancellationToken);
            }

            var parameterObject = new Dictionary<string, object?>(arguments, StringComparer.Ordinal);
            var wrappedArguments = new AIFunctionArguments(
                new Dictionary<string, object?> { [_parameterName] = parameterObject }
            )
            {
                Services = arguments.Services,
            };

            return base.InvokeCoreAsync(wrappedArguments, cancellationToken);
        }

        private static string DetectParameterName(JsonElement schema)
        {
            // Find the first property in "properties" that looks like an object parameter
            // (skip "cancellationToken" which may appear for async signatures)
            if (schema.TryGetProperty("properties", out var properties))
            {
                foreach (var prop in properties.EnumerateObject())
                {
                    if (string.Equals(prop.Name, "cancellationToken", StringComparison.OrdinalIgnoreCase))
                        continue;

                    return prop.Name;
                }
            }

            return "toolParams";
        }

        private static JsonElement FlattenSchema(JsonElement schema, string parameterName)
        {
            var rootNode = JsonNode.Parse(schema.GetRawText()) as JsonObject;
            if (
                rootNode?["properties"] is not JsonObject properties
                || !properties.TryGetPropertyValue(parameterName, out var parameterSchema)
                || parameterSchema is not JsonObject parameterObjectSchema
            )
            {
                return schema.Clone();
            }

            var flattened = parameterObjectSchema.DeepClone().AsObject();
            if (
                !flattened.ContainsKey("$defs")
                && rootNode.TryGetPropertyValue("$defs", out var definitions)
                && definitions is not null
            )
            {
                flattened["$defs"] = definitions.DeepClone();
            }

            return JsonSerializer.SerializeToElement(flattened);
        }
    }
}
