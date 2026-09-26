using Agw.Providers.Domain.Behaviors;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Exceptions;

namespace Agw.Projects.Tests;

public class AgwAiModelBehaviorTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(128_000, 0)]
    [InlineData(128_000, -1)]
    [InlineData(128_000, 128_000)]
    [InlineData(128_000, 256_000)]
    public void Define_InvalidTokenLimits_ThrowsInvalidParamWithoutChange(
        int maxContextWindowTokens,
        int maxOutputTokens
    )
    {
        var model = new AgwAiModel { Name = "original" };

        var exception = Assert.Throws<AgwException>(() =>
            new AgwAiModelBehavior(model).Define("updated", null, maxContextWindowTokens, maxOutputTokens)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Equal("original", model.Name);
    }

    [Fact]
    public void Define_ValidTokenLimits_AppliesDefinition()
    {
        var model = new AgwAiModel();

        new AgwAiModelBehavior(model).Define("gpt", "General model", 128_000, 16_000);

        Assert.Equal("gpt", model.Name);
        Assert.Equal("General model", model.Description);
        Assert.Equal(128_000, model.MaxContextWindowTokens);
        Assert.Equal(16_000, model.MaxOutputTokens);
    }
}
