using Agw.Shared.Utils;

namespace Agw.Shared.Tests.Utils;

public class PathUtilTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData(" ", " ")]
    [InlineData("./data", "./data")]
    [InlineData("~other/data", "~other/data")]
    public void ExpandTilde_WithoutHomePrefix_PreservesInput(string? path, string expected)
    {
        Assert.Equal(expected, PathUtil.ExpandTilde(path));
    }

    [Theory]
    [InlineData("~", "")]
    [InlineData("~/data", "data")]
    [InlineData("~\\data", "data")]
    public void ExpandTilde_WithHomePrefix_UsesCurrentOrExplicitHome(string path, string suffix)
    {
        var currentHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var explicitHome = Path.Combine(Path.GetTempPath(), "other-home");

        Assert.Equal(Path.Combine(currentHome, suffix), PathUtil.ExpandTilde(path));
        Assert.Equal(Path.Combine(explicitHome, suffix), PathUtil.ExpandTilde(path, explicitHome));
    }
}
