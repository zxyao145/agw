using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Agw.Shared.Data;

public static class ExtraJson
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static ValueConverter<T, string> Converter<T>() =>
        new(value => JsonSerializer.Serialize(value, Options), text => JsonSerializer.Deserialize<T>(text, Options)!);
}
