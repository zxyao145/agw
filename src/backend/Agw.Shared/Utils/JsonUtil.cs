using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace Agw.Shared.Utils;

public static class JsonUtil
{
    private static JsonSerializerOptions OPTIONS;

    static JsonUtil()
    {
        OPTIONS = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            //Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin, UnicodeRanges.CjkUnifiedIdeographs),
            AllowOutOfOrderMetadataProperties = true,
        };

        OPTIONS.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    }

    public static string Serialize<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, OPTIONS);
        return json;
    }

    public static T? Deserialize<T>(string value)
    {
        return JsonSerializer.Deserialize<T>(value, OPTIONS);
    }
}
