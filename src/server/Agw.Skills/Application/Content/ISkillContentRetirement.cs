using Agw.Skills.Contracts;

namespace Agw.Skills.Application.Content;

/// <summary>
/// <para>描述 Skill 内容目录及其规范化的相对路径。</para>
/// <para>Describes a Skill content directory and its canonical relative path.</para>
/// </summary>
public sealed record SkillContentLocation(string ContentPath, string Directory);

public interface ISkillContentRetirement
{
    /// <summary>
    /// <para>获取目录的内容使用权；归还时尝试清理已退役目录。</para>
    /// <para>Acquires a content lease for the directory and tries to clean up a retired directory on return.</para>
    /// </summary>
    Task<ILocalSkillDirectoryLease> AcquireAsync(SkillContentLocation location, CancellationToken cancellationToken);

    /// <summary>
    /// <para>写入退役标记，然后尝试清理目录。</para>
    /// <para>Writes the retirement marker, then tries to clean up the directory.</para>
    /// </summary>
    Task RetireAsync(SkillContentLocation location, CancellationToken cancellationToken);

    /// <summary>
    /// <para>清理 Skills 根目录中的退役目录。</para>
    /// <para>Cleans up retired directories under the Skills root.</para>
    /// </summary>
    Task SweepAsync(CancellationToken cancellationToken);
}
