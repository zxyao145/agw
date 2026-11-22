using Agw.Agents.Definitions.Contracts;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Agw.Host.OpenApi;

public sealed class A2ACredentialsSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken
    )
    {
        if (context.JsonTypeInfo.Type != typeof(A2AOriginCredentialsRequest))
            return Task.CompletedTask;

        var headers = schema.Properties?.TryGetValue("headers", out var property) == true ? property : null;
        // null 表示保留已保存的 header 值。
        // null keeps the saved header value.
        if (headers is OpenApiSchema { AdditionalProperties: OpenApiSchema headerValue })
        {
            headerValue.Type = JsonSchemaType.String | JsonSchemaType.Null;
        }
        return Task.CompletedTask;
    }
}
