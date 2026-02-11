using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexSdk;

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}

internal static class EnumExtensions
{
    public static string ToWireValue<TEnum>(this TEnum value)
        where TEnum : struct, Enum
    {
        var member = typeof(TEnum).GetMember(value.ToString()).FirstOrDefault();
        var enumMember = member?.GetCustomAttribute<EnumMemberAttribute>();
        return enumMember?.Value ?? value.ToString();
    }
}
