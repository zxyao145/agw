using Agw.Shared.Tooling;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Agw.Host.OpenApi;

/// <summary>
/// <para>与具体模块无关的 OpenAPI schema 规则：int 格式、多态判别字段，以及按属性声明推断 required。</para>
/// <para>Module-independent OpenAPI schema rules: int format, polymorphic discriminators, and required derived from property declarations.</para>
/// </summary>
public sealed class AgwOpenApiSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken
    )
    {
        var type = context.JsonTypeInfo.Type;
        if (type == typeof(int) || type == typeof(int?))
        {
            schema.Type = JsonSchemaType.Integer;
            schema.Format = "int32";
            schema.Pattern = null;
        }

        if (type != typeof(ToolValueObject) && typeof(ToolValueObject).IsAssignableFrom(type))
        {
            schema.Required ??= new HashSet<string>();
            schema.Required.Add("kind");
        }

        if (
            (type != typeof(ToolDefinition) && typeof(ToolDefinition).IsAssignableFrom(type))
            || (type != typeof(ToolBlockDefinition) && typeof(ToolBlockDefinition).IsAssignableFrom(type))
        )
        {
            schema.Required ??= new HashSet<string>();
            schema.Required.Add("name");
        }

        if (type.IsClass)
        {
            // 值类型与 string 属性按声明的可空性决定 required：`string?`、`Guid?` 可选，`string`、`bool` 必填。
            // Value-type and string properties derive required from declared nullability: `string?` and `Guid?` are optional, `string` and `bool` are required.
            foreach (var property in context.JsonTypeInfo.Properties)
            {
                var propertyType = property.PropertyType;
                if ((propertyType.IsValueType || propertyType == typeof(string)) && !property.IsGetNullable)
                {
                    schema.Required ??= new HashSet<string>();
                    schema.Required.Add(property.Name);
                }
            }
        }

        return Task.CompletedTask;
    }
}
