using System.Text.Json;
using Agw.Agents.Execution.Commands.Hitl;
using Agw.Agents.Execution.Messaging;
using Agw.Agents.Execution.Turns;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Agents;

namespace Agw.Agents.Tests;

public class ExecutionHumanInteractionChannelTests
{
    [Fact]
    public async Task RequestAsync_EmitsStructuredRequestAndWaitsForMatchingResponse()
    {
        var coordinator = new HumanGateApprovalCoordinator();
        var sink = new CapturingSink();
        var channel = new ExecutionHumanInteractionChannel(coordinator, sink);
        var payload = JsonSerializer.SerializeToElement(new { questions = Array.Empty<object>() });
        var pendingResponse = channel
            .RequestAsync(
                new HumanInteractionRequest("interaction-1", "questions", "Input needed", payload)
                {
                    ToolName = "ask_user_question",
                    CallId = "call-1",
                },
                TestContext.Current.CancellationToken
            )
            .AsTask();

        var message = await sink.MessageReceived.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(pendingResponse.IsCompleted);
        Assert.Equal("human-interaction-request", message.AdditionalProperties!["type"]);
        Assert.Equal("interaction-1", message.AdditionalProperties["requestId"]);
        Assert.Equal("questions", message.AdditionalProperties["interactionKind"]);
        Assert.Equal("ask_user_question", message.AdditionalProperties["toolName"]);
        Assert.Equal("call-1", message.AdditionalProperties["callId"]);
        Assert.Equal(payload, Assert.IsType<JsonElement>(message.AdditionalProperties["payload"]));

        var responseData = JsonSerializer.SerializeToElement(
            new { answers = new Dictionary<string, string> { ["Database?"] = "PostgreSQL" } }
        );
        Assert.True(
            await coordinator.TrySubmitAsync(
                new HumanResponseCommand("interaction-1", approved: true, responseData: responseData),
                TestContext.Current.CancellationToken
            )
        );

        var response = await pendingResponse;
        Assert.False(response.Cancelled);
        Assert.Equal(responseData, response.ResponseData);
    }

    private sealed class CapturingSink : IExecutionMessageSink
    {
        public TaskCompletionSource<AgwMessage> MessageReceived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken)
        {
            MessageReceived.TrySetResult(message);
            return ValueTask.CompletedTask;
        }
    }
}
