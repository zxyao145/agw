using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace Agw.Shared.Utils;

/// <summary>
/// <para>字符串 UTF-8 编码的 SHA-256：常见长度的字节放在栈上，超长输入使用 ArrayPool，不分配中间字节数组。</para>
/// <para>SHA-256 over a string's UTF-8 encoding: bytes of usual lengths stay on the stack and longer input uses ArrayPool, with no intermediate byte array.</para>
/// </summary>
public static class Sha256Util
{
    private const int StackLimit = 512;

    public static void HashUtf8(ReadOnlySpan<char> value, Span<byte> destination)
    {
        var maxByteCount = Encoding.UTF8.GetMaxByteCount(value.Length);
        byte[]? rented = null;
        Span<byte> buffer =
            maxByteCount <= StackLimit
                ? stackalloc byte[StackLimit]
                : (rented = ArrayPool<byte>.Shared.Rent(maxByteCount));
        try
        {
            var written = Encoding.UTF8.GetBytes(value, buffer);
            SHA256.HashData(buffer[..written], destination);
        }
        finally
        {
            if (rented != null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// <para>返回大写十六进制形式的哈希，与 Convert.ToHexString 的输出一致。</para>
    /// <para>Returns the hash as uppercase hexadecimal, matching Convert.ToHexString.</para>
    /// </summary>
    public static string HashUtf8Hex(ReadOnlySpan<char> value)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        HashUtf8(value, hash);
        return Convert.ToHexString(hash);
    }
}
