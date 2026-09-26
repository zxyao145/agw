using Agw.Shared.Data.Entities.Tools;
using Agw.Shared.Exceptions;
using Agw.Tools.Domain.Behaviors;

namespace Agw.Tools.Tests;

public class UserMemoryBehaviorTests
{
    [Fact]
    public void Define_PaddedValues_StoresTrimmedNameAndUpperCaseKey()
    {
        var memory = new UserMemory();

        new UserMemoryBehavior(memory).Define("  Coffee Order  ", "  Preferred drink  ", "Flat white");

        Assert.Equal("Coffee Order", memory.Name);
        Assert.Equal("COFFEE ORDER", memory.NormalizedName);
        Assert.Equal("Preferred drink", memory.Description);
        Assert.Equal("Flat white", memory.Content);
    }

    [Fact]
    public void Define_InvalidNameAndDescription_ReportsNameFirst()
    {
        var memory = new UserMemory();

        var exception = Assert.Throws<AgwException>(() =>
            new UserMemoryBehavior(memory).Define(" ", new string('d', 301), "content")
        );

        Assert.Equal(ErrorCodes.UserMemoryNameRequired.Code, exception.Code);
    }

    [Theory]
    [InlineData(null, "Existing")]
    [InlineData(" ", null)]
    [InlineData("Replaced", "Replaced")]
    public void RewriteByName_Description_KeepsOmittedAndClearsBlank(string? description, string? expected)
    {
        var memory = new UserMemory { Description = "Existing" };

        new UserMemoryBehavior(memory).RewriteByName("coffee order", "Oat latte", description);

        Assert.Equal(expected, memory.Description);
        Assert.Equal("coffee order", memory.Name);
        Assert.Equal("Oat latte", memory.Content);
    }

    [Fact]
    public void RewriteByName_BlankContent_ThrowsContentRequiredWithoutChange()
    {
        var memory = new UserMemory { Name = "coffee", Content = "Flat white" };

        var exception = Assert.Throws<AgwException>(() =>
            new UserMemoryBehavior(memory).RewriteByName("coffee", " ", null)
        );

        Assert.Equal(ErrorCodes.UserMemoryContentRequired.Code, exception.Code);
        Assert.Equal("Flat white", memory.Content);
    }
}
