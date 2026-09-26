using Agw.Shared.Exceptions;

namespace Agw.Tools.Domain.Behaviors;

public sealed class UserMemoryBehavior
{
    private const int MaxNameLength = 64;
    private const int MaxDescriptionLength = 300;

    private readonly UserMemory _memory;

    public UserMemoryBehavior(UserMemory memory)
    {
        _memory = memory;
    }

    /// <summary>
    /// <para>记忆名称去除两端空白后保存；按大写形式比较，因此同一用户的名称不区分大小写。</para>
    /// <para>A memory name is stored trimmed and compared in upper case, so one user's names ignore case.</para>
    /// </summary>
    public static (string Display, string Normalized) NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new AgwException(ErrorCodes.UserMemoryNameRequired);
        }

        var display = name.Trim();
        if (display.Length > MaxNameLength)
        {
            throw new AgwException(ErrorCodes.UserMemoryNameTooLong);
        }

        return (display, display.ToUpperInvariant());
    }

    public void Define(string name, string? description, string content)
    {
        var (display, normalized) = NormalizeName(name);
        var normalizedDescription = NormalizeDescription(description);
        EnsureContent(content);
        Apply(display, normalized, normalizedDescription, content);
    }

    /// <summary>
    /// <para>按名称改写已有记忆：省略描述时保留原描述，空白描述清除原描述。</para>
    /// <para>Rewrites an existing memory by name: an omitted description keeps the old one, and a blank description clears it.</para>
    /// </summary>
    public void RewriteByName(string name, string content, string? description)
    {
        var (display, normalized) = NormalizeName(name);
        EnsureContent(content);
        var normalizedDescription = description == null ? _memory.Description : NormalizeDescription(description);
        Apply(display, normalized, normalizedDescription, content);
    }

    private void Apply(string display, string normalized, string? description, string content)
    {
        _memory.Name = display;
        _memory.NormalizedName = normalized;
        _memory.Description = description;
        _memory.Content = content;
    }

    private static void EnsureContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new AgwException(ErrorCodes.UserMemoryContentRequired);
        }
    }

    private static string? NormalizeDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var normalized = description.Trim();
        if (normalized.Length > MaxDescriptionLength)
        {
            throw new AgwException(ErrorCodes.UserMemoryDescriptionTooLong);
        }

        return normalized;
    }
}
