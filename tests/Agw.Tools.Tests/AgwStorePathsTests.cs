using Agw.Shared.Exceptions;
using Agw.Tools.Impl.ToolBlocks.Storage;

namespace Agw.Tools.Tests;

public sealed class AgwStorePathsTests
{
    [Fact]
    public void NormalizeRelativePath_MixedSeparators_ReturnsForwardSlashPath()
    {
        Assert.Equal("docs/sub/guide.md", AgwStorePaths.NormalizeRelativePath("docs\\\\sub//guide.md/"));
    }

    [Fact]
    public void NormalizeRelativePath_EmptyDirectory_ReturnsRoot()
    {
        Assert.Equal(string.Empty, AgwStorePaths.NormalizeRelativePath(" ", isDirectory: true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/etc/passwd")]
    [InlineData("\\share\\file.txt")]
    [InlineData("C:/secret.txt")]
    [InlineData("docs/../secret.txt")]
    [InlineData("docs/./guide.md")]
    public void NormalizeRelativePath_InvalidFilePath_ThrowsInvalidParam(string path)
    {
        var exception = Assert.Throws<AgwException>(() => AgwStorePaths.NormalizeRelativePath(path));

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }
}
