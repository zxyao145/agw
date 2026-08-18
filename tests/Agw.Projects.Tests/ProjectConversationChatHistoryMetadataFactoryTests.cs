using Agw.Projects.Domain.Services;
using Agw.Shared.Contracts.Projects;

using Microsoft.Extensions.AI;

namespace Agw.Projects.Tests;

public class ProjectConversationChatHistoryMetadataFactoryTests
{
    [Fact]
    public void FromMessage_CopiesTargetMetadataFromTextContent()
    {
        var message = new ChatMessage(
            ChatRole.User,
            [
                new TextContent("hello")
                {
                    AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        ["targetType"] = "agentflow",
                        ["targetId"] = "11111111-1111-1111-1111-111111111111"
                    }
                }
            ]);

        var metadata = ProjectConversationChatHistoryMetadataFactory.FromMessage(message);

        Assert.NotNull(metadata);
        Assert.Equal("agentflow", metadata!["targetType"].GetString());
        Assert.Equal("11111111-1111-1111-1111-111111111111", metadata["targetId"].GetString());
    }

    [Fact]
    public void FromMessage_CopiesConversationHandoffCursorFromMessageMetadata()
    {
        var message = new ChatMessage(ChatRole.User, "continue")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [ConversationHandoffMetadata.ThroughSequenceKey] = 42L
            }
        };

        var metadata = ProjectConversationChatHistoryMetadataFactory.FromMessage(message);

        Assert.NotNull(metadata);
        Assert.Equal(
            42,
            metadata![ConversationHandoffMetadata.ThroughSequenceKey].GetInt64());
    }

    [Fact]
    public void Create_UsesTrimmedInputPrefix()
    {
        var title = TaskTitleFactory.Create("  this is a chat title  ");

        Assert.Equal("this is a chat title", title);
    }
}
