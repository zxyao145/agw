using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Agw.Files.Infrastructure.Storage.Confined;

/// <summary>
/// 通过 <see cref="RandomAccess"/> 在已打开的文件句柄上按偏移读写整段文本；句柄不会被关闭，读取后仍可用同一个句柄写回。
/// Reads and writes whole text on an already opened file handle by offset through <see cref="RandomAccess"/>; the
/// handle is never closed, so the same handle can be written back after reading.
/// </summary>
internal static class HandleTextIO
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 读取整个文件；编码按 BOM 判断，默认 UTF-8。
    /// Reads the whole file; the encoding is detected from the BOM and defaults to UTF-8.
    /// </summary>
    public static async Task<string> ReadAllTextAsync(SafeFileHandle handle, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        long offset = 0;
        while (true)
        {
            var read = await RandomAccess.ReadAsync(handle, chunk, offset, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
            offset += read;
        }

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 用不带 BOM 的 UTF-8 覆盖整个文件。
    /// Replaces the whole file with UTF-8 text without a BOM.
    /// </summary>
    public static async Task WriteAllTextAsync(SafeFileHandle handle, string content, CancellationToken ct)
    {
        var bytes = Utf8WithoutBom.GetBytes(content);
        await RandomAccess.WriteAsync(handle, bytes, 0, ct).ConfigureAwait(false);
        RandomAccess.SetLength(handle, bytes.Length);
    }
}
