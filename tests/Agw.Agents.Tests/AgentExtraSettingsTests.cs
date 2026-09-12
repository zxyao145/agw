using Agw.Agents.Definitions.Agents;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Tests;

public sealed class AgentExtraSettingsTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("  {\"model\":\"test\"}  ", "{\"model\":\"test\"}")]
    public void Normalize_ValidInput_PreservesObjectOrClearsBlank(string? input, string? expected) =>
        Assert.Equal(expected, AgentExtraSettings.Normalize(input));

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("null")]
    public void Normalize_NonObject_ThrowsSharedError(string input) =>
        Assert.Equal(
            ErrorCodes.InvalidAgentExtraSettings.Code,
            Assert.Throws<AgwException>(() => AgentExtraSettings.Normalize(input)).Code
        );
}
