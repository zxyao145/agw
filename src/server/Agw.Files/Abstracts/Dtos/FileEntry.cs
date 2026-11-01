namespace Agw.Files.Abstracts.Dtos;

public sealed record FileEntry(string Path, bool IsDirectory, long Size, DateTimeOffset LastModifiedUtc)
{
    /// <summary>
    /// <para>条目本身是否为符号链接或联接点（仅名称层面的元数据）。</para>
    /// <para>Whether the entry itself is a symbolic link or junction, based on entry metadata only.</para>
    /// </summary>
    public bool IsSymbolicLink { get; init; }
}
