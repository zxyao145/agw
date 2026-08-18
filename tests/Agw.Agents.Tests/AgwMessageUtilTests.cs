using Agw.Agents.Execution;
using Agw.Shared;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

public class AgwMessageUtilTests
{
    [Fact]
    public void CreateExecutionInputMessages_HandoffAndCurrentInput_PreservesOrderAndMetadata()
    {
        var targetId = Guid.CreateVersion7();
        var handoffMessage = new ChatMessage(ChatRole.Assistant, "previous plan") { MessageId = "handoff-message" };
        ConversationHandoffMetadata.MarkHandoffMessage(handoffMessage);
        var input = new AgwUserInput
        {
            MessageId = "current-message",
            Author = "requester",
            Contents =
            [
                new AgwTextContent
                {
                    Content = "implement it",
                    AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        ["existing"] = "kept",
                        ["targetType"] = "wrong",
                    },
                },
            ],
        };

        var messages = AgwMessageUtil.CreateExecutionInputMessages(
            input,
            AgentRuntimeType.Agentflow,
            targetId,
            new ConversationHandoff([handoffMessage], 41)
        );

        Assert.Equal(["previous plan", "implement it"], messages.Select(message => message.Text));
        Assert.Equal("current-message", messages[1].MessageId);
        Assert.Equal("requester", messages[1].AuthorName);
        Assert.Equal(41L, messages[1].AdditionalProperties![ConversationHandoffMetadata.ThroughSequenceKey]);
        var text = Assert.IsType<TextContent>(Assert.Single(messages[1].Contents));
        Assert.Equal("kept", text.AdditionalProperties!["existing"]);
        Assert.Equal("agentflow", text.AdditionalProperties["targetType"]);
        Assert.Equal(targetId.ToString("D"), text.AdditionalProperties["targetId"]);
    }

    [Fact]
    public void CreateUserChatMessage_TextAndUri_PreservesSupportedContentProperties()
    {
        var input = new AgwUserInput
        {
            MessageId = "message-1",
            Contents =
            [
                new AgwTextContent
                {
                    Content = "inspect",
                    AdditionalProperties = new AdditionalPropertiesDictionary { ["text"] = "metadata" },
                },
                new AgwUriContent(new Uri("https://example.com/file"), "text/plain")
                {
                    AdditionalProperties = new AdditionalPropertiesDictionary { ["uri"] = "metadata" },
                },
            ],
        };

        var message = AgwMessageUtil.CreateUserChatMessage(input);

        Assert.Equal("message-1", message.MessageId);
        Assert.Equal(Constants.DefaultInputAuthor, message.AuthorName);
        Assert.Equal("metadata", Assert.IsType<TextContent>(message.Contents[0]).AdditionalProperties!["text"]);
        Assert.Equal("metadata", Assert.IsType<UriContent>(message.Contents[1]).AdditionalProperties!["uri"]);
    }
}
