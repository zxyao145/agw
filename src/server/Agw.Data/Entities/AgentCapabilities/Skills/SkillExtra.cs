namespace Agw.Shared.Data.Entities.Skills;

public sealed record SkillExtra
{
    public string? SourceDigest { get; init; }

    // 用于确认同目录更新是否已提交。
    // Identifies the committed contents when the directory path stays the same.
    public Guid? ContentRevision { get; init; }
}
