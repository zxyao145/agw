namespace Agw.Tools.Impl.ToolBlocks.Storage;

/// <summary>
/// Provides an abstract base class for file storage operations.
/// 文件存储操作的抽象基类。
/// </summary>
/// <remarks>
/// <para>
/// All paths are relative to an implementation-defined root, use forward slashes as separators, and must not escape
/// the root (e.g., via <c>..</c> segments). Each implementation enforces this.
/// 所有路径都相对于实现定义的根目录，使用正斜杠分隔，且不得越出根目录（例如通过 <c>..</c> 段），由各实现负责校验。
/// </para>
/// <para>
/// <see cref="SearchAsync"/> must number matches over the content <see cref="ReadAsync"/> returns, treating
/// <c>\n</c>, <c>\r\n</c>, or a lone <c>\r</c> as a line terminator, the same split <see cref="AgwFileEditor"/> uses.
/// Otherwise grep and the line editor disagree and a line edit lands on a line the caller never saw.
/// <see cref="SearchAsync"/> 必须基于 <see cref="ReadAsync"/> 返回的内容编号，并把 <c>\n</c>、<c>\r\n</c> 或单独的 <c>\r</c>
/// 视为行结束符，与 <see cref="AgwFileEditor"/> 的切分规则一致；否则 grep 与按行编辑的行号不一致，编辑会作用到调用方没见过的行。
/// </para>
/// </remarks>
public abstract class AgwAgentFileStore
{
    /// <summary>
    /// Writes content to a file, creating or overwriting it.
    /// 写入文件内容，文件不存在时创建，存在时覆盖。
    /// </summary>
    public abstract Task WriteAsync(string path, string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the content of a file, or returns <see langword="null"/> when the file does not exist.
    /// 读取文件内容；文件不存在时返回 <see langword="null"/>。
    /// </summary>
    public abstract Task<string?> ReadAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a file and returns whether it existed.
    /// 删除文件，并返回文件在删除前是否存在。
    /// </summary>
    public abstract Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the direct children of a directory, subdirectories before files. Use an empty string for the root.
    /// 列出目录的直接子项，子目录排在文件之前；空字符串表示根目录。
    /// </summary>
    public abstract Task<IReadOnlyList<AgwFileStoreEntry>> ListChildrenAsync(
        string directory,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Checks whether a file exists.
    /// 检查文件是否存在。
    /// </summary>
    public abstract Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches file contents with a case-insensitive regular expression.
    /// 使用不区分大小写的正则表达式搜索文件内容。
    /// </summary>
    /// <param name="directory">The relative directory to search; an empty string searches the root.</param>
    /// <param name="regexPattern">The case-insensitive regular expression matched against each line.</param>
    /// <param name="globPattern">
    /// An optional glob matched against each file's path relative to <paramref name="directory"/>; <c>**</c> matches
    /// across subdirectories.
    /// </param>
    /// <param name="recursive">Whether descendant files are searched in addition to direct children.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>Results whose <see cref="AgwFileSearchResult.FileName"/> is relative to <paramref name="directory"/>.</returns>
    public abstract Task<IReadOnlyList<AgwFileSearchResult>> SearchAsync(
        string directory,
        string regexPattern,
        string? globPattern = null,
        bool recursive = false,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Ensures a directory exists, creating it if necessary.
    /// 确保目录存在，不存在时创建。
    /// </summary>
    public abstract Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);
}
