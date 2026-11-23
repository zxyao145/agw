using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Agw.Shared.Data;

public static class ExtraJson
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// extra 列的转换：record 与 camelCase 的 JSON 文本。
    /// Conversion for extra columns: a record and its camelCase JSON text.
    /// </summary>
    public static ValueConverter<T, string> Converter<T>()
        where T : class =>
        new(value => JsonSerializer.Serialize(value, Options), json => JsonSerializer.Deserialize<T>(json, Options)!);
}
