using System.IO.Enumeration;
using System.Security.Cryptography;
using Agw.Files.Infrastructure.Storage.Confined;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Agw.Files.Infrastructure.Storage;

/// <summary>
/// 三个 IAgwFileSystem 实现的原子提交共用的部分：临时文件的命名与识别、内容哈希、复制并刷新到磁盘、写入前提的对比，以及
/// 删除未提交的临时文件。
/// The parts of the atomic commit shared by the three IAgwFileSystem implementations: naming and recognizing temporary
/// files, content hashing, copying and flushing to disk, comparing the write precondition, and deleting an
/// uncommitted temporary file.
/// </summary>
internal static class AtomicFileWrite
{
    private const string TemporaryMarker = ".agw-";
    private const string TemporarySuffix = ".tmp";
    private const int TemporaryIdLength = 32;
    private const int CopyBufferSize = 81920;

    /// <summary>
    /// 临时文件名与 <see cref="FilePathLocks"/> 使用相同的大小写规则：同一个文件身份的不同大小写写法共用一个锁，也共用
    /// 临时文件。
    /// Temporary file names follow the same letter-case rule as <see cref="FilePathLocks"/>: spellings of one file
    /// identity that differ only in case share one lock and therefore share temporary files.
    /// </summary>
    private static readonly StringComparison FileNameComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>
    /// 为 <paramref name="fileName"/> 生成同一目录中的临时文件名：<c>.{文件名}.agw-{32 位十六进制}.tmp</c>。
    /// Creates the temporary file name for <paramref name="fileName"/> in the same directory:
    /// <c>.{file name}.agw-{32 hex digits}.tmp</c>.
    /// </summary>
    public static string CreateTemporaryName(string fileName) =>
        $".{fileName}{TemporaryMarker}{Guid.NewGuid():N}{TemporarySuffix}";

    /// <summary>
    /// 判断 <paramref name="name"/> 是否是 <paramref name="fileName"/> 以前留下的临时文件。
    /// Decides whether <paramref name="name"/> is a temporary file left behind earlier for <paramref name="fileName"/>.
    /// </summary>
    public static bool IsTemporaryNameOf(ReadOnlySpan<char> name, string fileName)
    {
        var idStart = 1 + fileName.Length + TemporaryMarker.Length;
        if (name.Length != idStart + TemporaryIdLength + TemporarySuffix.Length || name[0] != '.')
        {
            return false;
        }

        if (
            !name.Slice(1, fileName.Length).Equals(fileName, FileNameComparison)
            || !name.Slice(1 + fileName.Length, TemporaryMarker.Length).SequenceEqual(TemporaryMarker)
            || !name[^TemporarySuffix.Length..].SequenceEqual(TemporarySuffix)
        )
        {
            return false;
        }

        foreach (var character in name.Slice(idStart, TemporaryIdLength))
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 列出 <paramref name="directoryPath"/> 中 <paramref name="fileName"/> 以前留下的临时文件名；以点开头的隐藏文件同样列出。
    /// Lists the names of temporary files left behind earlier for <paramref name="fileName"/> in
    /// <paramref name="directoryPath"/>; hidden files starting with a dot are listed as well.
    /// </summary>
    public static string[] FindTemporaryFiles(string directoryPath, string fileName) =>
        new FileSystemEnumerable<string>(
            directoryPath,
            (ref FileSystemEntry entry) => entry.FileName.ToString(),
            new EnumerationOptions { AttributesToSkip = 0 }
        )
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                !entry.IsDirectory && IsTemporaryNameOf(entry.FileName, fileName),
        }.ToArray();

    /// <summary>
    /// 计算句柄所指文件全部字节的 SHA-256（64 位小写十六进制）。句柄交给读取流，返回前关闭。
    /// Computes the SHA-256 of every byte of the file behind the handle (64 lowercase hex digits). The handle is handed
    /// to the reading stream and closed before returning.
    /// </summary>
    public static async Task<string> HashAsync(SafeFileHandle handle, CancellationToken ct)
    {
        await using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 0);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// 把 <paramref name="content"/> 从当前位置起的全部内容写入临时文件句柄并刷新到磁盘；句柄由调用方释放。
    /// Writes everything from the current position of <paramref name="content"/> to the temporary file handle and
    /// flushes it to disk; the caller disposes the handle.
    /// </summary>
    public static async Task CopyAsync(Stream content, SafeFileHandle handle, CancellationToken ct)
    {
        var buffer = new byte[CopyBufferSize];
        long offset = 0;
        int read;
        while ((read = await content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), offset, ct).ConfigureAwait(false);
            offset += read;
        }

        RandomAccess.FlushToDisk(handle);
    }

    /// <summary>
    /// 磁盘上文件的哈希与写入前提不同时抛出 <c>FileChanged</c>。
    /// Throws <c>FileChanged</c> when the hash of the file on disk differs from the write precondition.
    /// </summary>
    public static void VerifyHash(string expectedSha256, string actualSha256, string path)
    {
        if (!string.Equals(expectedSha256, actualSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw FileChanged(path);
        }
    }

    /// <summary>
    /// 复核：文件标识、大小或修改时间与哈希检查时不同时抛出 <c>FileChanged</c>。
    /// The re-check: throws <c>FileChanged</c> when the identity, size or modification time differs from the hash
    /// check.
    /// </summary>
    public static void VerifyVersion(FileVersion expected, FileVersion actual, string path)
    {
        if (expected != actual)
        {
            throw FileChanged(path);
        }
    }

    public static AgwException FileChanged(string path) =>
        new(ErrorCodes.FileChanged, $"File '{path}' changed since it was read. Reload it and try again.");

    public static AgwException AlreadyExists(string path) =>
        new(ErrorCodes.FileAlreadyExists, $"'{path}' already exists.");

    public static AgwException NotWritable(string path) =>
        new(ErrorCodes.FileNotWritable, $"'{path}' is read-only or not writable.");

    /// <summary>
    /// 删除未提交的临时文件。删除失败只记录警告，正在抛出的原始异常继续抛出。
    /// Deletes an uncommitted temporary file. A failed delete is only logged as a warning, so the original exception
    /// being thrown keeps propagating.
    /// </summary>
    public static void DeleteTemporaryFile(Action delete, string path, string temporaryName, ILogger logger)
    {
        try
        {
            delete();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or AgwException)
        {
            logger.LogWarning(
                exception,
                "Failed to delete temporary file {TemporaryFileName} of an unfinished write to {FilePath}",
                temporaryName,
                path
            );
        }
    }
}

/// <summary>
/// 哈希检查时记录、提交前复核的文件状态：文件标识、大小与修改时间。
/// The file state recorded at the hash check and re-checked before committing: identity, size and modification time.
/// </summary>
internal readonly record struct FileVersion(FileIdentity Identity, long Size, DateTimeOffset LastModifiedUtc);
