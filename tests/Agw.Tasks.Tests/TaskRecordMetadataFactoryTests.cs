using Agw.Tasks.Domain.Services;

using Microsoft.Extensions.AI;

namespace Agw.Tasks.Tests;

public class TaskRecordMetadataFactoryTests
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

        var metadata = TaskRecordMetadataFactory.FromMessage(message);

        Assert.NotNull(metadata);
        Assert.Equal("agentflow", metadata!["targetType"].GetString());
        Assert.Equal("11111111-1111-1111-1111-111111111111", metadata["targetId"].GetString());
    }

    [Fact]
    public void Create_UsesTrimmedInputPrefix()
    {
        var title = ProjectTaskTitleFactory.Create("  this is a chat title  ");

        Assert.Equal("this is a chat title", title);
    }
}
