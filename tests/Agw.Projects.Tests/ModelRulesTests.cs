using Agw.Providers.Domain.Rules;
using Agw.Shared.Exceptions;

namespace Agw.Projects.Tests;

public class ModelRulesTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(128_000, 0)]
    [InlineData(128_000, -1)]
    [InlineData(128_000, 128_000)]
    [InlineData(128_000, 256_000)]
    public void ValidateTokenLimits_InvalidValues_ThrowsInvalidParam(int maxContextWindowTokens, int maxOutputTokens)
    {
        var exception = Assert.Throws<AgwException>(() =>
            ModelRules.ValidateTokenLimits(maxContextWindowTokens, maxOutputTokens)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }
}
