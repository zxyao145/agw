using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace Agw.Shared.Utils;

/// <summary>
/// <para>Agw 的 JSON 约定：属性名 camelCase，枚举写成 camelCase 字符串，中文等非 ASCII 字符原样输出。</para>
/// <para>The Agw JSON convention: camelCase property names, camelCase string enums, and CJK and other non-ASCII characters emitted as-is.</para>
/// <para>只需要 System.Text.Json Web 默认行为时改用 <see cref="WebJsonOptions"/>，两者产生的 JSON 不可互换。</para>
/// <para>Use <see cref="WebJsonOptions"/> for plain System.Text.Json Web defaults; the two produce JSON that cannot be interchanged.</para>
/// </summary>
public static class JsonUtil
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        AllowOutOfOrderMetadataProperties = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, Options);
    }

    public static T? Deserialize<T>(string value)
    {
        return JsonSerializer.Deserialize<T>(value, Options);
    }

    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8Json)
    {
        return JsonSerializer.Deserialize<T>(utf8Json, Options);
    }
}
