using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Agw.Shared.Data;

/// <summary>
/// 把枚举保存为小写名称文本，例如 Running 保存为 running。
/// Stores an enum as its lowercase name, for example Running as running.
/// </summary>
public static class LowercaseEnum
{
    public static ValueConverter<TEnum, string> Converter<TEnum>()
        where TEnum : struct, Enum =>
        new(value => value.ToString().ToLowerInvariant(), text => Enum.Parse<TEnum>(text, true));
}
