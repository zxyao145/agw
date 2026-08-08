using Agw.Shared.AgwMsgVm;
using Agw.Shared.Extensions;

using Microsoft.Extensions.AI;

namespace Agw.Shared.Tests;

public sealed class AgentRunResponseUpdateExtensionsTests
{
    [Fact]
    public void ToAiMessage_StringFunctionResult_PreservesPlainText()
    {
        var message = new ChatMessage(
            ChatRole.Tool,
            [new FunctionResultContent("call-1", "Mode changed to \"execute\".")]);

        var result = message.ToAiMessage();

        var content = Assert.IsType<AgwFunctionResultContent>(Assert.Single(result!.Contents));
        Assert.Equal("Mode changed to \"execute\".", content.Content);
    }

    [Fact]
    public void ToAiMessage_StructuredFunctionResult_SerializesJson()
    {
        var message = new ChatMessage(
            ChatRole.Tool,
            [new FunctionResultContent("call-1", new { mode = "execute" })]);

        var result = message.ToAiMessage();

        var content = Assert.IsType<AgwFunctionResultContent>(Assert.Single(result!.Contents));
        Assert.Equal("{\"mode\":\"execute\"}", content.Content);
    }
}
