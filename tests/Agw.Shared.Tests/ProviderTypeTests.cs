using System.Text.Json;

using Agw.Shared.Data.Entities.Providers;

namespace Agw.Shared.Tests;

public class ProviderTypeTests
{
    [Fact]
    public void Values_HaveExpectedNamesAndNumbers()
    {
        Assert.Equal(
            ["OpenAIChatCompletions", "OpenAIResponses", "Anthropic"],
            Enum.GetNames<ProviderType>());
        Assert.Equal([0, 1, 2], Enum.GetValues<ProviderType>().Select(value => (int)value));
    }

    [Theory]
    [InlineData(0, "\"OpenAIChatCompletions\"")]
    [InlineData(1, "\"OpenAIResponses\"")]
    [InlineData(2, "\"Anthropic\"")]
    public void JsonSerialization_UsesExpectedName(int value, string expectedJson)
    {
        var json = JsonSerializer.Serialize((ProviderType)value);

        Assert.Equal(expectedJson, json);
    }
}
