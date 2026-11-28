namespace Agw.Skills.Contracts;

public interface ILocalSkillContentResolver
{
    /// <summary>
    /// <para>获取 Local Skill 当前内容目录的使用权，并在归还前保留该目录。</para>
    /// <para>Acquires a lease on the Local Skill's current content directory and keeps it present until return.</para>
    /// </summary>
    Task<ILocalSkillDirectoryLease?> AcquireDirectoryAsync(Guid skillId, CancellationToken cancellationToken = default);
}

public interface ILocalSkillDirectoryLease : IAsyncDisposable
{
    /// <summary>
    /// <para>获取由此内容租用保留的目录路径。</para>
    /// <para>Gets the directory path kept by this content lease.</para>
    /// </summary>
    string Path { get; }
}
