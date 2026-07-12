using Agw.Agents.Runtime.Agentflows;
using Agw.Shared;
using Agw.Shared.AgwMsgVm;

using Microsoft.Extensions.AI;

using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agw.Agents.Tests;

public class AgentflowRuntimeServiceTests
{
    [Fact]
    public void CreateWorkflowOutputMessages_ListOfChatMessages_ReturnsAgwMessages()
    {
        var output = new List<ChatMessage>
        {
            new(ChatRole.Assistant, "Bonjour")
            {
                AuthorName = "french-translator",
            },
        };

        var messages = AgentflowRuntimeService.CreateWorkflowOutputMessages(output);

        var message = Assert.Single(messages);
        Assert.Equal("french-translator", message.Author);
        var content = Assert.IsType<AgwTextContent>(Assert.Single(message.Contents));
        Assert.Equal("Bonjour", content.Content);
    }

    [Fact]
    public void CreateWorkflowInputMessages_SetsDefaultUserAuthor()
    {
        var input = "Translate Hello World";

        var messages = AgentflowRuntimeService.CreateWorkflowInputMessages(input);

        var message = Assert.Single(messages);
        Assert.Equal(ChatRole.User, message.Role);
        Assert.Equal(Constants.DefaultInputAuthor, message.AuthorName);
        Assert.Equal(input, message.Text);
    }
}
