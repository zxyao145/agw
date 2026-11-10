using Agw.Agents.Execution.Commands.Exec;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Commands.Steer;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Tests;

public partial class ExecutionCommandHandlerTests
{
    [Fact]
    public async Task SteerCommand_TurnInterruptedBeforeDelivery_ReturnsInputBeforeTurnFinished()
    {
        var sink = new CapturingSink();
        Runtimes.HoldTurnOpen = true;
        await using var context = CreateContext(CreateTask("steer"), sink: sink);
        var exec = CreateExecCommand(Guid.CreateVersion7());
        await context.StartTurnAsync(exec, TestContext.Current.CancellationToken);
        await Runtimes.TurnStarts.WaitAsync(TurnTimeout, TestContext.Current.CancellationToken);
        var steerId = Guid.CreateVersion7();

        await new SteerCommandHandler().HandleAsync(
            CreateSteer(exec, steerId, "use the new API"),
            context,
            TestContext.Current.CancellationToken
        );
        await InterruptAndWaitAsync(context);

        var returnedIndex = sink.Messages.FindIndex(message => IsMessageType(message, AgwMessageTypes.SteerReturned));
        var finishedIndex = sink.Messages.FindIndex(AgwMessageClassifier.IsTurnFinished);
        Assert.True(returnedIndex >= 0);
        Assert.True(returnedIndex < finishedIndex);
        var returned = sink.Messages[returnedIndex];
        Assert.Equal(steerId.ToString("D"), returned.AdditionalProperties!["steerId"]);
        Assert.Equal("turn-interrupted", returned.AdditionalProperties["reason"]);
        Assert.Equal("use the new API", Assert.IsType<AgwTextContent>(Assert.Single(returned.Contents)).Content);
        Assert.DoesNotContain(sink.Messages, message => IsMessageType(message, AgwMessageTypes.SteerDelivered));
    }

    [Fact]
    public async Task SteerCommand_TurnFinishesItsResponse_ContinuesTheSameTurnWithTheInput()
    {
        var sink = new CapturingSink();
        Runtimes.HoldTurnOpen = true;
        await using var context = CreateContext(CreateTask("steer"), sink: sink);
        var exec = CreateExecCommand(Guid.CreateVersion7());
        await context.StartTurnAsync(exec, TestContext.Current.CancellationToken);
        await Runtimes.TurnStarts.WaitAsync(TurnTimeout, TestContext.Current.CancellationToken);
        var steerId = Guid.CreateVersion7();
        await context.SteerTurnAsync(
            CreateSteer(exec, steerId, "and add a test"),
            TestContext.Current.CancellationToken
        );

        // 第一次模型调用结束后继续同一个 Turn；第二次调用不再等待。
        // The turn continues after the first model call; the second call does not wait.
        Runtimes.HoldTurnOpen = false;
        Runtimes.ReleaseHeldTurns();
        await context.WhenIdleAsync();

        var userMessage = Assert.Single(sink.Messages, message => message.Role == AiRole.User);
        Assert.Equal(steerId.ToString("D"), userMessage.MessageId);
        var delivered = Assert.Single(sink.Messages, message => IsMessageType(message, AgwMessageTypes.SteerDelivered));
        Assert.Equal(steerId.ToString("D"), delivered.AdditionalProperties!["steerId"]);
        Assert.DoesNotContain(sink.Messages, message => IsMessageType(message, AgwMessageTypes.SteerReturned));
        var finished = Assert.Single(sink.Messages, AgwMessageClassifier.IsTurnFinished);
        Assert.Equal("completed", finished.AdditionalProperties!["status"]);
    }

    [Fact]
    public async Task SteerCommand_SameSteerIdTwice_IsReturnedOnce()
    {
        var sink = new CapturingSink();
        Runtimes.HoldTurnOpen = true;
        await using var context = CreateContext(CreateTask("steer"), sink: sink);
        var exec = CreateExecCommand(Guid.CreateVersion7());
        await context.StartTurnAsync(exec, TestContext.Current.CancellationToken);
        await Runtimes.TurnStarts.WaitAsync(TurnTimeout, TestContext.Current.CancellationToken);
        var steer = CreateSteer(exec, Guid.CreateVersion7(), "once");

        await context.SteerTurnAsync(steer, TestContext.Current.CancellationToken);
        await context.SteerTurnAsync(steer, TestContext.Current.CancellationToken);
        await InterruptAndWaitAsync(context);

        Assert.Single(sink.Messages, message => IsMessageType(message, AgwMessageTypes.SteerReturned));
    }

    [Fact]
    public async Task SteerCommand_BeyondPendingLimit_ThrowsLimitExceeded()
    {
        Runtimes.HoldTurnOpen = true;
        await using var context = CreateContext(CreateTask("steer"));
        var exec = CreateExecCommand(Guid.CreateVersion7());
        await context.StartTurnAsync(exec, TestContext.Current.CancellationToken);
        await Runtimes.TurnStarts.WaitAsync(TurnTimeout, TestContext.Current.CancellationToken);
        for (var index = 0; index < 8; index++)
        {
            await context.SteerTurnAsync(
                CreateSteer(exec, Guid.CreateVersion7(), $"steer {index}"),
                TestContext.Current.CancellationToken
            );
        }

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            context.SteerTurnAsync(
                CreateSteer(exec, Guid.CreateVersion7(), "one too many"),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(ErrorCodes.SteerLimitExceeded.Code, exception.Code);
        await InterruptAndWaitAsync(context);
    }

    [Fact]
    public async Task SteerCommand_WithoutActiveTurn_ThrowsNoActiveTurn()
    {
        await using var context = CreateContext(CreateTask("steer"));
        var exec = CreateExecCommand(Guid.CreateVersion7());
        await context.ApplySettingsAsync(
            SettingCommandMapper.FromCommand(new SettingCommand(Guid.CreateVersion7(), exec.ConversationId!.Value)),
            TestContext.Current.CancellationToken
        );

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            context.SteerTurnAsync(
                CreateSteer(exec, Guid.CreateVersion7(), "nobody listens"),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(ErrorCodes.SteerNoActiveTurn.Code, exception.Code);
    }

    [Fact]
    public async Task SteerCommand_OtherTurnId_ThrowsNoActiveTurn()
    {
        Runtimes.HoldTurnOpen = true;
        await using var context = CreateContext(CreateTask("steer"));
        var exec = CreateExecCommand(Guid.CreateVersion7());
        await context.StartTurnAsync(exec, TestContext.Current.CancellationToken);
        await Runtimes.TurnStarts.WaitAsync(TurnTimeout, TestContext.Current.CancellationToken);
        var other = new SteerCommand(
            exec.ConversationId!.Value,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            new AgwUserInput { Contents = [new AgwTextContent { Content = "stale" }] }
        );

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            context.SteerTurnAsync(other, TestContext.Current.CancellationToken)
        );

        Assert.Equal(ErrorCodes.SteerNoActiveTurn.Code, exception.Code);
        await InterruptAndWaitAsync(context);
    }

    [Fact]
    public async Task SteerCommand_OtherConversation_ThrowsInvalidParam()
    {
        Runtimes.HoldTurnOpen = true;
        await using var context = CreateContext(CreateTask("steer"));
        var exec = CreateExecCommand(Guid.CreateVersion7());
        await context.StartTurnAsync(exec, TestContext.Current.CancellationToken);
        await Runtimes.TurnStarts.WaitAsync(TurnTimeout, TestContext.Current.CancellationToken);
        var other = new SteerCommand(
            Guid.CreateVersion7(),
            exec.TurnId!.Value,
            Guid.CreateVersion7(),
            new AgwUserInput { Contents = [new AgwTextContent { Content = "wrong conversation" }] }
        );

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            context.SteerTurnAsync(other, TestContext.Current.CancellationToken)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        await InterruptAndWaitAsync(context);
    }

    [Theory]
    [InlineData("conversation")]
    [InlineData("turn")]
    [InlineData("steer")]
    [InlineData("input")]
    public async Task SteerCommandHandler_MissingField_ThrowsInvalidParam(string missing)
    {
        await using var context = CreateContext(CreateTask("steer"));
        var command = new SteerCommand(
            missing == "conversation" ? Guid.Empty : Guid.CreateVersion7(),
            missing == "turn" ? Guid.Empty : Guid.CreateVersion7(),
            missing == "steer" ? Guid.Empty : Guid.CreateVersion7(),
            new AgwUserInput { Contents = missing == "input" ? [] : [new AgwTextContent { Content = "text" }] }
        );

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            new SteerCommandHandler().HandleAsync(command, context, TestContext.Current.CancellationToken)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    private static async Task InterruptAndWaitAsync(ExecutionConnectionContext context)
    {
        await context.InterruptTurnAsync(null, "stop", TestContext.Current.CancellationToken);
        await context.WhenIdleAsync();
    }

    private static SteerCommand CreateSteer(ExecCommand exec, Guid steerId, string text) =>
        new(
            exec.ConversationId!.Value,
            exec.TurnId ?? Guid.CreateVersion7(),
            steerId,
            new AgwUserInput { Contents = [new AgwTextContent { Content = text }] }
        );

    private static bool IsMessageType(AgwMessage message, string type) =>
        string.Equals(AgwMessageClassifier.GetMessageType(message), type, StringComparison.Ordinal);
}
