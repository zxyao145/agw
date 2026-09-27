using Agw.Files.Abstracts.Dtos;

namespace Agw.Tools.Impl.ToolBlocks.Storage;

/// <summary>
/// Provides an abstract base class for the storage behind the file and memory tools.
/// 文件工具与记忆工具所用存储的抽象基类。
/// </summary>
/// <remarks>
/// <para>
/// All paths are relative to an implementation-defined root and use forward slashes as separators. File-backed
/// implementations delegate every file operation, including path validation, to <see cref="IAgwFileSystem"/>.
/// 所有路径都相对于实现定义的根目录并使用正斜杠分隔；基于文件的实现把所有文件操作（包括路径校验）交给 <see cref="IAgwFileSystem"/>。
/// </para>
/// <para>
/// <see cref="SearchAsync"/> must number matches the way <see cref="TextContentEditor"/> splits lines, treating
/// <c>\n</c>, <c>\r\n</c>, or a lone <c>\r</c> as a line terminator. Otherwise grep and the line editor disagree and a
/// line edit lands on a line the caller never saw.
/// <see cref="SearchAsync"/> 必须按 <see cref="TextContentEditor"/> 的切分规则编号，把 <c>\n</c>、<c>\r\n</c> 或单独的 <c>\r</c>
/// 视为行结束符；否则 grep 与按行编辑的行号不一致，编辑会作用到调用方没见过的行。
/// </para>
/// </remarks>
public abstract class AgwAgentFileStore
{
    /// <summary>
    /// Writes content to the specified file, replacing its content.
    /// 把内容写入指定文件并覆盖原有内容。
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
    /// Checks whether the specified file exists.
    /// 判断是否存在指定的文件。
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
    /// <returns>Results whose <see cref="AgwFileSearchResult.FileName"/> is relative to the store root.</returns>
    public abstract Task<IReadOnlyList<AgwFileSearchResult>> SearchAsync(
        string directory,
        string regexPattern,
        string? globPattern = null,
        bool recursive = false,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Replaces text by <see cref="TextContentEditor.ApplyReplace"/> and returns the replacement count, or
    /// <see langword="null"/> when the file does not exist.
    /// 按 <see cref="TextContentEditor.ApplyReplace"/> 的规则替换文本并返回替换次数；文件不存在时返回 <see langword="null"/>。
    /// </summary>
    public abstract Task<int?> ReplaceTextAsync(
        string path,
        string oldString,
        string newString,
        bool replaceAll,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Applies whole-line edits by <see cref="TextContentEditor.ApplyReplaceLines"/> and returns whether the file
    /// existed.
    /// 按 <see cref="TextContentEditor.ApplyReplaceLines"/> 的规则整行替换，并返回文件是否存在。
    /// </summary>
    public abstract Task<bool> ReplaceLinesAsync(
        string path,
        IReadOnlyList<AgwFileLineEdit> edits,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Creates the specified directory.
    /// 创建指定目录。
    /// </summary>
    public abstract Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);
}
