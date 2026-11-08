using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Agw.Shared.Exceptions;

namespace Agw.Extensions.Infrastructure.Runtime;

internal sealed class ExtensionJsonTypeInfoResolver : IJsonTypeInfoResolver
{
    private readonly IReadOnlyList<JsonSerializerContext> _contexts;
    private readonly DefaultJsonTypeInfoResolver _defaultResolver = new();

    public ExtensionJsonTypeInfoResolver(IReadOnlyList<JsonSerializerContext> contexts)
    {
        _contexts = contexts;
    }

    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        foreach (JsonSerializerContext context in _contexts)
        {
            JsonTypeInfo? typeInfo = ((IJsonTypeInfoResolver)context).GetTypeInfo(type, options);
            if (typeInfo is not null)
            {
                return typeInfo;
            }
        }

        if (ContainsCollectibleType(type))
        {
            throw new AgwException(
                ErrorCodes.ExtensionPackageInvalid,
                "Source-generated JSON metadata must be registered for collectible extension types."
            );
        }

        return _defaultResolver.GetTypeInfo(type, options);
    }

    private static bool ContainsCollectibleType(Type type)
    {
        if (type.IsCollectible)
        {
            return true;
        }

        if (type.HasElementType && type.GetElementType() is { } elementType && ContainsCollectibleType(elementType))
        {
            return true;
        }

        return type.IsGenericType && type.GetGenericArguments().Any(ContainsCollectibleType);
    }
}
