using Agw.Shared.Exceptions;

namespace Agw.Shared.Domain.Rules;

/// <summary>
/// <para>环境变量名称的规范化与校验规则，供各模块 Behavior 共用。</para>
/// <para>Normalization and validation rules for environment variable names, shared by module Behaviors.</para>
/// </summary>
public static class EnvironmentVariableRules
{
    /// <summary>
    /// <para>去掉名称两端空白后校验非空、唯一且不含 '=' 与空字符，返回规范化后的新字典。</para>
    /// <para>Trims names, requires them to be non-empty, unique, and free of '=' and null characters, and returns a new normalized dictionary.</para>
    /// </summary>
    /// <param name="environmentVariables">
    /// <para>待规范化的环境变量集合；为空时返回空字典。</para>
    /// <para>Environment variables to normalize; null yields an empty dictionary.</para>
    /// </param>
    /// <param name="invalidNameErrorCode">
    /// <para>名称不合法时抛出的模块错误码。</para>
    /// <para>Module error code thrown when a name is invalid.</para>
    /// </param>
    /// <returns>
    /// <para>按 Ordinal 比较的规范化环境变量字典。</para>
    /// <para>Normalized environment variables compared with StringComparer.Ordinal.</para>
    /// </returns>
    public static Dictionary<string, string> Normalize(
        IEnumerable<KeyValuePair<string, string>>? environmentVariables,
        ErrorCode invalidNameErrorCode
    )
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in environmentVariables ?? [])
        {
            var normalizedName = name.Trim();
            if (
                string.IsNullOrEmpty(normalizedName)
                || normalizedName.Contains('=')
                || normalizedName.Contains('\0')
                || !normalized.TryAdd(normalizedName, value ?? string.Empty)
            )
            {
                throw new AgwException(invalidNameErrorCode);
            }
        }

        return normalized;
    }
}
