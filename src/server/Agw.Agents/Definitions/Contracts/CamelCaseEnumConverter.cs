using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agw.Agents.Definitions.Contracts;

public sealed class CamelCaseEnumConverter<TEnum> : JsonStringEnumConverter<TEnum>
    where TEnum : struct, Enum
{
    public CamelCaseEnumConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false) { }
}
