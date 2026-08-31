using Microsoft.Extensions.AI;
using PiAgentSdk.MAF.Internal;
using Xunit;

namespace PiAgentSdk.MAF.Tests;

public sealed class PiMafPromptBuilderTests
{
    [Fact]
    public void Create_AssistantHandoffAndUserInput_PreservesBothInOrder()
    {
        // Arrange
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, "previous result"),
            new(ChatRole.User, "continue now"),
        };

        // Act
        var prompt = PiMafPromptBuilder.Create(messages);

        // Assert
        Assert.NotNull(prompt);
        Assert.True(
            prompt.Text.IndexOf("previous result", StringComparison.Ordinal)
                < prompt.Text.IndexOf("continue now", StringComparison.Ordinal)
        );
        Assert.Contains("[assistant]", prompt.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[user]", prompt.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_ImageOnlyInput_UsesNeutralPromptAndImage()
    {
        // Arrange
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, [new DataContent(new byte[] { 1, 2, 3 }, "image/png")]),
        };

        // Act
        var prompt = PiMafPromptBuilder.Create(messages);

        // Assert
        Assert.NotNull(prompt);
        Assert.False(string.IsNullOrWhiteSpace(prompt.Text));
        Assert.Equal("AQID", Assert.Single(prompt.Images).Data);
    }

    [Fact]
    public void Create_PrivateReasoning_DoesNotEnterPrompt()
    {
        // Arrange
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new TextReasoningContent("private"), new TextContent("public")]),
            new(ChatRole.User, "next"),
        };

        // Act
        var prompt = PiMafPromptBuilder.Create(messages);

        // Assert
        Assert.DoesNotContain("private", prompt!.Text);
        Assert.Contains("public", prompt.Text);
    }
}
