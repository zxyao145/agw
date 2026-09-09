using System.Text.Json;
using Agw.Agents.Execution.HumanInteraction.InProcess;

namespace Agw.Agents.Tests;

public class UserInputChannelTests
{
    [Fact]
    public async Task RequestAsync_EmitsStructuredRequestAndWaitsForMatchingResponse()
    {
        var sink = new InteractionTestSink();
        var session = new InProcessInteractionSession(sink);
        var payload = JsonSerializer.SerializeToElement(new { questions = Array.Empty<object>() });
        var pending = session
            .RequestAsync(
                new UserInputRequest("questions", "Input needed", payload)
                {
                    Source = new InteractionSource { ToolName = "ask_user_question", CallId = "call-1" },
                },
                TestContext.Current.CancellationToken
            )
            .AsTask();
        var message = Assert.Single(sink.Messages);
        var request = Assert.IsType<UserInputInteraction>(InteractionTestData.Read(message));
        Assert.Equal("interaction-request", message.AdditionalProperties!["type"]);
        Assert.Equal("questions", request.InputKind);
        Assert.Equal("ask_user_question", request.Source.ToolName);
        Assert.Equal("call-1", request.Source.CallId);
        Assert.Equal(payload.GetRawText(), request.Payload.GetRawText());
        Assert.False(pending.IsCompleted);
        var data = JsonSerializer.SerializeToElement(
            new { answers = new Dictionary<string, string> { ["Database?"] = "PostgreSQL" } }
        );
        await session.TrySubmitAsync(
            new UserInputResponse
            {
                InteractionId = request.InteractionId,
                Cancelled = false,
                ResponseData = data,
            },
            TestContext.Current.CancellationToken
        );
        var response = await pending;
        Assert.Equal(request.InteractionId, response.InteractionId);
        Assert.False(response.Cancelled);
        Assert.Equal(data, response.ResponseData);
    }
}
