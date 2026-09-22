using System.Text.Json;

namespace Agw.Shared.Utils;

/// <summary>
/// <para>System.Text.Json Web 默认选项的共享实例：属性名 camelCase，枚举写成数字，非 ASCII 字符按 JSON 转义。</para>
/// <para>Shared instance of the System.Text.Json Web defaults: camelCase property names, numeric enums, and JSON-escaped non-ASCII characters.</para>
/// <para>需要枚举字符串与原样输出中文时改用 <see cref="JsonUtil"/>，两者产生的 JSON 不可互换。</para>
/// <para>Use <see cref="JsonUtil"/> when string enums and unescaped CJK output are required; the two produce JSON that cannot be interchanged.</para>
/// </summary>
public static class WebJsonOptions
{
    /// <summary>
    /// <para>只读共享实例；需要额外转换器或解析器的调用方自行新建选项。</para>
    /// <para>Read-only shared instance; callers needing extra converters or resolvers construct their own options.</para>
    /// </summary>
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web);
}
