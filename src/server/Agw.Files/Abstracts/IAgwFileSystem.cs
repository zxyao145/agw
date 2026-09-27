using Agw.Files.Abstracts.Dtos;

namespace Agw.Files.Abstracts;

/// <summary>
/// 对文件系统的抽象。实现应该持有一个根目录，所有的操作都是在根目录下进行，同时注意防止路径逃逸。
/// </summary>
public interface IAgwFileSystem
{
    /// <summary>
    /// 判断是否存在指定的文件。
    /// Checks whether the specified file exists.
    /// </summary>
    Task<bool> ExistsFileAsync(string path, CancellationToken ct);

    /// <summary>
    /// 判断是否存在指定的目录。
    /// Checks whether the specified directory exists.
    /// </summary>
    Task<bool> ExistsDirectoryAsync(string path, CancellationToken ct);

    /// <summary>
    /// 获取指定文件或目录的信息。
    /// Gets information about the specified file or directory.
    /// </summary>
    Task<FileEntry?> StatAsync(string path, CancellationToken ct);

    /// <summary>
    /// 读取指定文件的全部文本。
    /// Reads all text of the specified file.
    /// </summary>
    Task<string> ReadAllTextAsync(string path, CancellationToken ct);

    /// <summary>
    /// 读取指定文件的所有行，每行去掉结束符。
    /// Reads all lines of the specified file, with line terminators removed.
    /// </summary>
    Task<string[]> ReadAllLinesAsync(string path, CancellationToken ct);

    /// <summary>
    /// 把文本写入指定文件并覆盖原有内容，缺失的父目录会一并创建。
    /// Writes text to the specified file, replacing its content and creating missing parent directories.
    /// </summary>
    Task WriteAllTextAsync(string path, string content, CancellationToken ct);

    /// <summary>
    /// 创建指定目录及缺失的父目录。
    /// Creates the specified directory and any missing parent directories.
    /// </summary>
    Task CreateDirectoryAsync(string path, CancellationToken ct);

    /// <summary>
    /// 删除指定的文件，或递归删除指定的目录；空路径表示根目录本身。
    /// Deletes the specified file, or the specified directory recursively; an empty path means the root itself.
    /// </summary>
    Task DeleteAsync(string path, CancellationToken ct);

    /// <summary>
    /// 枚举指定目录下名称与 <paramref name="searchPattern"/>（<c>*</c>、<c>?</c> 通配符）匹配的文件和子目录，递归时跳过符号链接；
    /// 返回的 Path 相对于本文件系统的根目录。
    /// Enumerates files and subdirectories under the specified directory whose names match
    /// <paramref name="searchPattern"/> (<c>*</c> and <c>?</c> wildcards), skipping symbolic links when recursive; the
    /// returned Path is relative to this file system's root.
    /// </summary>
    IAsyncEnumerable<FileEntry> EnumerateAsync(string path, string searchPattern, bool recursive, CancellationToken ct);

    /// <summary>
    /// 按行搜索文件内容，每个有命中的文件产出一个结果；FileName 相对于本文件系统根目录，Snippet 是第一条命中的行。
    /// Searches file contents line by line and yields one result per file with matches; FileName is relative to this
    /// file system's root and Snippet is the first matching line.
    /// </summary>
    IAsyncEnumerable<AgwFileSearchResult> SearchAsync(string rootPath, SearchOptions options, CancellationToken ct);

    /// <summary>
    /// 创建文本文件；文件已存在时不写入并返回 false。
    /// Creates a text file; returns false without writing when the file already exists.
    /// </summary>
    Task<bool> CreateTextFileAsync(string path, string content, CancellationToken ct);

    /// <summary>
    /// 按 <see cref="TextContentEditor.SliceLines"/> 的规则读取从 1 开始、首尾都包含的行区间，每行保留结束符。
    /// Reads the 1-based inclusive line range by <see cref="TextContentEditor.SliceLines"/>, each line keeping its
    /// terminator.
    /// </summary>
    Task<IReadOnlyList<string>> ReadLinesAsync(string path, int startLine, int? endLine, CancellationToken ct);

    /// <summary>
    /// 按 <see cref="TextContentEditor.ApplyReplace"/> 的规则替换文件中的文本，并返回替换次数。
    /// Replaces text in the file by <see cref="TextContentEditor.ApplyReplace"/> and returns the replacement count.
    /// </summary>
    Task<int> ReplaceTextAsync(string path, string oldString, string newString, bool replaceAll, CancellationToken ct);

    /// <summary>
    /// 按 <see cref="TextContentEditor.ApplyReplaceLines"/> 的规则整行替换文件内容。
    /// Applies whole-line edits to the file by <see cref="TextContentEditor.ApplyReplaceLines"/>.
    /// </summary>
    Task ReplaceLinesAsync(string path, IReadOnlyList<AgwFileLineEdit> edits, CancellationToken ct);

    /// <summary>
    /// 返回以相对目录 <paramref name="path"/> 为根的文件系统，传给它的路径不能越出该目录；目录不需要已经存在。
    /// Returns a file system rooted at the relative directory <paramref name="path"/>; paths passed to it cannot escape
    /// that directory, which does not need to exist yet.
    /// </summary>
    IAgwFileSystem GetSubFileSystem(string path);
}
