using System.Text.Json;
using Agw.Extensions.Contracts;
using Agw.Shared.Exceptions;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace Agw.Host.OpenApi;

public sealed class ExtensionOpenApiDocumentTransformer : IOpenApiDocumentTransformer
{
    private readonly IExtensionGenerationCatalog _catalog;

    public ExtensionOpenApiDocumentTransformer(IExtensionGenerationCatalog catalog)
    {
        _catalog = catalog;
    }

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken
    )
    {
        foreach (
            var generation in _catalog
                .GetSnapshots()
                .Where(generation => generation.State == ExtensionGenerationState.Active)
        )
        foreach (var endpoint in generation.Endpoints)
        {
            var path = $"/api/ext/{generation.Name}/{endpoint.RelativePath}";
            var item =
                document.Paths.TryGetValue(path, out var existing) && existing is OpenApiPathItem current
                    ? current
                    : new OpenApiPathItem();
            var responseSchema = endpoint.ResponseSchema is { } schema
                ? ParseSchema(schema, document)
                : new OpenApiSchema();
            var operation = new OpenApiOperation
            {
                OperationId = $"extension_{generation.Name}_{endpoint.Id}",
                Summary = endpoint.Summary,
                Parameters =
                [
                    new OpenApiParameter
                    {
                        Name = "X-Agw-Extension-Id",
                        In = ParameterLocation.Header,
                        Required = true,
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                    },
                ],
                Responses = new OpenApiResponses
                {
                    ["200"] = new OpenApiResponse
                    {
                        Description = "Extension response",
                        Content = new Dictionary<string, OpenApiMediaType>
                        {
                            ["application/json"] = new()
                            {
                                Schema = new OpenApiSchema
                                {
                                    Type = JsonSchemaType.Object,
                                    Properties = new Dictionary<string, IOpenApiSchema>
                                    {
                                        ["code"] = new OpenApiSchema { Type = JsonSchemaType.Integer },
                                        ["title"] = new OpenApiSchema { Type = JsonSchemaType.String },
                                        ["statusCode"] = new OpenApiSchema { Type = JsonSchemaType.Integer },
                                        ["data"] = responseSchema,
                                    },
                                },
                            },
                        },
                    },
                },
            };
            if (endpoint.RequestSchema is { } requestSchema)
                operation.RequestBody = new OpenApiRequestBody
                {
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/json"] = new() { Schema = ParseSchema(requestSchema, document) },
                    },
                };
            item.AddOperation(new HttpMethod(endpoint.Method), operation);
            document.Paths[path] = item;
        }
        return Task.CompletedTask;
    }

    private static IOpenApiSchema ParseSchema(JsonElement element, OpenApiDocument document)
    {
        var schema = OpenApiModelFactory.Parse<OpenApiSchema>(
            element.GetRawText(),
            OpenApiSpecVersion.OpenApi3_1,
            document,
            out var diagnostic,
            "json"
        );
        if (schema is null || diagnostic.Errors.Count > 0)
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Extension endpoint schema is invalid.");
        return schema;
    }
}
