using Agw.Shared.Exceptions;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Agw.Tools.Impl.ToolBlocks.Storage;

/// <summary>
/// Normalizes relative store paths and matches glob patterns for the file tools.
/// 为文件工具规范化相对存储路径并匹配 glob 模式。
/// </summary>
internal static class AgwStorePaths
{
    /// <summary>
    /// Converts backslashes to forward slashes, trims leading and trailing separators, and collapses consecutive
    /// separators. Rooted paths, drive roots, and <c>.</c>/<c>..</c> segments are rejected.
    /// 把反斜杠转换为正斜杠，去掉首尾分隔符并合并连续分隔符；拒绝根路径、盘符以及 <c>.</c>/<c>..</c> 段。
    /// </summary>
    /// <param name="path">The relative path to normalize.</param>
    /// <param name="isDirectory">Whether the path is a directory, for which an empty result means the root.</param>
    internal static string NormalizeRelativePath(string path, bool isDirectory = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            if (!isDirectory)
            {
                throw InvalidParameter("A file path must not be empty or whitespace-only.");
            }

            return string.Empty;
        }

        var normalized = path.Replace('\\', '/').Trim('/');
        if (
            Path.IsPathRooted(path)
            || path.StartsWith('/')
            || path.StartsWith('\\')
            || (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':')
        )
        {
            throw InvalidParameter(
                $"Invalid path: '{path}'. Paths must be relative and must not start with '/', '\\', or a drive root."
            );
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(static segment => segment is "." or ".."))
        {
            throw InvalidParameter($"Invalid path: '{path}'. Paths must not contain '.' or '..' segments.");
        }

        var result = string.Join('/', segments);
        if (!isDirectory && result.Length == 0)
        {
            throw InvalidParameter("A file path must not be empty.");
        }

        return result;
    }

    /// <summary>
    /// Creates a case-insensitive <see cref="Matcher"/> for a glob pattern such as <c>*.md</c>.
    /// 为 <c>*.md</c> 这类 glob 模式创建不区分大小写的 <see cref="Matcher"/>。
    /// </summary>
    internal static Matcher CreateGlobMatcher(string globPattern)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(globPattern);
        return matcher;
    }

    /// <summary>
    /// Returns whether <paramref name="name"/> matches <paramref name="matcher"/>; a <see langword="null"/> matcher
    /// matches every name.
    /// 判断 <paramref name="name"/> 是否匹配 <paramref name="matcher"/>；<paramref name="matcher"/> 为 <see langword="null"/> 时匹配所有名称。
    /// </summary>
    internal static bool MatchesGlob(string name, Matcher? matcher) => matcher?.Match(name).HasMatches ?? true;

    private static AgwException InvalidParameter(string message) => new(ErrorCodes.InvalidParam, message);
}
