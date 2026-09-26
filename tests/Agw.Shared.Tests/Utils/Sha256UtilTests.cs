using System.Security.Cryptography;
using System.Text;
using Agw.Shared.Utils;

namespace Agw.Shared.Tests.Utils;

public class Sha256UtilTests
{
    [Theory]
    [InlineData("")]
    [InlineData("agw_0123456789abcdefghijklmnopqrstuvwxyzABCDEFG")]
    [InlineData("中文与 emoji 😀")]
    public void HashUtf8Hex_ShortInput_MatchesUtf8Sha256(string value)
    {
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))),
            Sha256Util.HashUtf8Hex(value)
        );
    }

    [Fact]
    public void HashUtf8_InputBeyondStackBuffer_MatchesUtf8Sha256()
    {
        var value = string.Concat(Enumerable.Repeat("多字节文本 multi-byte text ", 200));
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];

        Sha256Util.HashUtf8(value, hash);

        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(value)), hash.ToArray());
    }
}
