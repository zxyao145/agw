using Agw.Agents.Execution.Agents.Runtime;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

public sealed class AgentRuntimeModeTests
{
    [Theory]
    [InlineData("plan")]
    [InlineData("execute")]
    public void CreateModeStatusMessage_CreatesHiddenPersistedSnapshot(string mode)
    {
        var message = AgentRuntimeFactory.CreateModeStatusMessage(mode);

        Assert.Equal(ChatRole.System, message.Role);
        Assert.Equal("tools", message.AuthorName);
        Assert.False(string.IsNullOrWhiteSpace(message.MessageId));
        Assert.Equal(string.Empty, Assert.IsType<TextContent>(Assert.Single(message.Contents)).Text);
        Assert.NotNull(message.AdditionalProperties);
        Assert.Equal(AgwMessageTypes.ToolModeStatus, message.AdditionalProperties["type"]);
        Assert.Equal(mode, message.AdditionalProperties["mode"]);
        Assert.Equal("control", message.AdditionalProperties["presentation"]);
    }
}
