using System.Text.Json;
using Agw.Shared.Exceptions;
using Agw.Tools.HumanInteraction;
using Agw.Tools.Impl.Basic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Tools.Tests;

public class AskUserQuestionToolTests
{
    [Fact]
    public async Task InvokeAsync_BeforeHumanResponse_RemainsPendingAndIgnoresModelAnswer()
    {
        var channel = new TestHumanInteractionChannel();
        await using var services = CreateServices(channel);
        var function = Assert.IsType<HumanInteractionRequiredAIFunction>(new AskUserQuestionTool().ToAITool());
        var arguments = CreateArguments(services, forgedAnswer: "SQLite");

        var pendingResult = function.InvokeAsync(arguments, TestContext.Current.CancellationToken).AsTask();
        var request = await channel.RequestReceived.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(pendingResult.IsCompleted);
        Assert.Equal("questions", request.InteractionKind);
        Assert.Equal("ask_user_question", request.ToolName);
        Assert.Null(request.CallId);
        Assert.Equal(
            "Which database should we use?",
            request.Payload.GetProperty("questions")[0].GetProperty("question").GetString()
        );

        channel.Submit(
            new HumanInteractionResponse(
                request.RequestId,
                Cancelled: false,
                JsonSerializer.SerializeToElement(
                    new
                    {
                        answers = new Dictionary<string, string> { ["Which database should we use?"] = "PostgreSQL" },
                    }
                )
            )
        );

        var result = Assert.IsType<JsonElement>(await pendingResult);
        Assert.Equal(
            "PostgreSQL",
            result.GetProperty("answers").GetProperty("Which database should we use?").GetString()
        );
        Assert.DoesNotContain("SQLite", result.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task InvokeAsync_DuringFunctionInvocation_IncludesFunctionCallIdentity()
    {
        var channel = new TestHumanInteractionChannel();
        await using var services = CreateServices(channel);
        var function = Assert.IsAssignableFrom<AIFunction>(new AskUserQuestionTool().ToAITool());
        FunctionInvocationContextBridge.SetCurrent(
            new FunctionInvocationContext
            {
                CallContent = new FunctionCallContent("call-1", "ask_user_question", new Dictionary<string, object?>()),
            }
        );

        try
        {
            var pendingResult = function
                .InvokeAsync(CreateArguments(services), TestContext.Current.CancellationToken)
                .AsTask();
            var request = await channel.RequestReceived.Task.WaitAsync(TestContext.Current.CancellationToken);

            Assert.Equal("ask_user_question", request.ToolName);
            Assert.Equal("call-1", request.CallId);

            channel.Submit(new HumanInteractionResponse(request.RequestId, Cancelled: true, ResponseData: null));
            await pendingResult;
        }
        finally
        {
            FunctionInvocationContextBridge.SetCurrent(null);
        }
    }

    [Fact]
    public async Task InvokeAsync_WhenHumanCancels_ReturnsCancelledResult()
    {
        var channel = new TestHumanInteractionChannel();
        await using var services = CreateServices(channel);
        var function = Assert.IsAssignableFrom<AIFunction>(new AskUserQuestionTool().ToAITool());
        var pendingResult = function
            .InvokeAsync(CreateArguments(services), TestContext.Current.CancellationToken)
            .AsTask();
        var request = await channel.RequestReceived.Task.WaitAsync(TestContext.Current.CancellationToken);

        channel.Submit(new HumanInteractionResponse(request.RequestId, Cancelled: true, ResponseData: null));

        var result = Assert.IsType<AskUserQuestionToolResult>(await pendingResult);
        Assert.True(result.Cancelled);
        Assert.Empty(result.Answers);
    }

    [Fact]
    public async Task InvokeAsync_WithoutInteractiveChannel_FailsInsteadOfContinuing()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        var function = Assert.IsAssignableFrom<AIFunction>(new AskUserQuestionTool().ToAITool());

        var exception = await Assert.ThrowsAsync<AgwException>(async () =>
            await function.InvokeAsync(CreateArguments(services), TestContext.Current.CancellationToken)
        );

        Assert.Equal(ErrorCodes.AgentExecutionFailed.Code, exception.Code);
        Assert.Contains("requires an active interactive channel", exception.Message);
    }

    private static ServiceProvider CreateServices(IHumanInteractionChannel channel) =>
        new ServiceCollection()
            .AddSingleton<IHumanInteractionContextAccessor>(new TestContextAccessor(channel))
            .BuildServiceProvider();

    private static AIFunctionArguments CreateArguments(IServiceProvider services, string? forgedAnswer = null)
    {
        var question = new AskUserQuestionQuestion
        {
            Question = "Which database should we use?",
            Header = "Database",
            Options =
            [
                new AskUserQuestionOption { Label = "PostgreSQL", Description = "Use the production database." },
                new AskUserQuestionOption { Label = "SQLite", Description = "Use a local database." },
            ],
        };
        var values = new Dictionary<string, object?> { ["questions"] = new[] { question } };
        if (forgedAnswer != null)
        {
            values["answers"] = new Dictionary<string, string> { [question.Question] = forgedAnswer };
        }

        return new AIFunctionArguments(values) { Services = services };
    }

    private sealed class TestContextAccessor : IHumanInteractionContextAccessor
    {
        public TestContextAccessor(IHumanInteractionChannel current)
        {
            Current = current;
        }

        public IHumanInteractionChannel? Current { get; }
    }

    private sealed class TestHumanInteractionChannel : IHumanInteractionChannel
    {
        private readonly TaskCompletionSource<HumanInteractionResponse> _response = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public TaskCompletionSource<HumanInteractionRequest> RequestReceived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<HumanInteractionResponse> RequestAsync(
            HumanInteractionRequest request,
            CancellationToken cancellationToken
        )
        {
            RequestReceived.TrySetResult(request);
            return await _response.Task.WaitAsync(cancellationToken);
        }

        public void Submit(HumanInteractionResponse response) => _response.TrySetResult(response);
    }

    private sealed class FunctionInvocationContextBridge : FunctionInvokingChatClient
    {
        private FunctionInvocationContextBridge(IChatClient innerClient)
            : base(innerClient) { }

        public static void SetCurrent(FunctionInvocationContext? context) => CurrentContext = context;
    }
}
