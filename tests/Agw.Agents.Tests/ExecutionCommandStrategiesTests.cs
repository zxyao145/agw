using System.Net.WebSockets;

using Agw.Api.Contracts;
using Agw.Api.Execution;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Shared.Models;

using Microsoft.AspNetCore.Mvc;

namespace Agw.Agents.Tests;

public class ExecutionCommandStrategiesTests
{
    [Fact]
    public async Task SettingStrategy_WhenSettingContentInvalid_SendsError()
    {
        var strategy = new SettingCommandStrategy();
        var state = new ExecutionConnectionState();
        string? errorMessage = null;
        var coordinator = new FakeAgentExecutionCoordinator
        {
            NormalizeSettingsAsyncImpl = command =>
            {
                throw new InvalidOperationException("should not be called");
            }
        };
        var context = CreateContext(
            state: state,
            coordinator: coordinator,
            sendErrorAsync: message =>
            {
                errorMessage = message;
                return Task.CompletedTask;
            });

        await strategy.ExecuteAsync(
            new SettingCommand(Guid.NewGuid(), Guid.NewGuid(), null, "[]"),
            context);

        Assert.Equal("SettingContent must be a JSON object string.", errorMessage);
        Assert.False(coordinator.NormalizeCalled);
        Assert.Null(state.CurrentSettings);
    }

    [Fact]
    public async Task SettingStrategy_WhenSettingContentValid_UpdatesCurrentSettings()
    {
        var strategy = new SettingCommandStrategy();
        var normalizedSetting = new SettingCommand(Guid.NewGuid(), Guid.NewGuid(), null, """{"cwd":"D:/source/repos/agw"}""")
        {
            Resume = true
        };
        var state = new ExecutionConnectionState();
        var coordinator = new FakeAgentExecutionCoordinator
        {
            NormalizeSettingsAsyncImpl = _ => Task.FromResult(normalizedSetting)
        };
        var context = CreateContext(
            state: state,
            coordinator: coordinator);

        await strategy.ExecuteAsync(
            new SettingCommand(Guid.NewGuid(), Guid.NewGuid(), null, """{"cwd":"D:/source/repos/agw"}"""),
            context);

        Assert.NotNull(state.CurrentSettings);
        Assert.Equal(normalizedSetting.TaskId, state.CurrentSettings!.TaskId);
        Assert.True(state.CurrentSettings.Resume);
    }

    [Fact]
    public async Task ExecStrategy_WhenExecutionRunning_SendsBusyErrorAndSkipsResolve()
    {
        var strategy = new ExecCommandStrategy();
        var state = new ExecutionConnectionState();
        using var executionCts = new CancellationTokenSource();
        var pendingExecution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.TryStartExecution(new ActiveExecution(pendingExecution.Task, executionCts));

        string? errorMessage = null;
        var coordinator = new FakeAgentExecutionCoordinator
        {
            ResolveTaskAsyncImpl = (_, _) =>
            {
                throw new InvalidOperationException("should not be called");
            }
        };
        var context = CreateContext(
            state: state,
            coordinator: coordinator,
            sendErrorAsync: message =>
            {
                errorMessage = message;
                return Task.CompletedTask;
            });

        await strategy.ExecuteAsync(CreateExecCommand(), context);

        Assert.Equal("当前任务未执行完毕，请稍候再执行。", errorMessage);
        Assert.False(coordinator.ResolveCalled);
    }

    [Fact]
    public async Task InterruptStrategy_WhenNoActiveExecution_SendsSystemMessage()
    {
        var strategy = new InterruptCommandStrategy();
        string? systemMessage = null;
        var context = CreateContext(
            sendSystemMessageAsync: message =>
            {
                systemMessage = message;
                return Task.CompletedTask;
            });

        await strategy.ExecuteAsync(new InterruptCommand { Reason = "稍后再试" }, context);

        Assert.Equal("稍后再试", systemMessage);
    }

    [Fact]
    public async Task Dispatcher_WhenCommandIsUnsupported_ClosesConnection()
    {
        var dispatcher = new ExecutionCommandDispatcher(
            [new SettingCommandStrategy(), new ExecCommandStrategy(), new InterruptCommandStrategy()]);

        WebSocketCloseStatus? closeStatus = null;
        string? closeReason = null;
        var context = CreateContext(
            closeConnectionAsync: (status, reason) =>
            {
                closeStatus = status;
                closeReason = reason;
                return Task.CompletedTask;
            });

        var result = await dispatcher.DispatchAsync(new UnknownCommand(), context);

        Assert.True(result.CloseConnection);
        Assert.Equal(WebSocketCloseStatus.InvalidPayloadData, closeStatus);
        Assert.Equal("Not Support Payload", closeReason);
    }

    private static ExecutionCommandContext CreateContext(
        ExecutionConnectionState? state = null,
        IAgentExecutionCoordinator? coordinator = null,
        Func<string, Task>? sendErrorAsync = null,
        Func<string, Task>? sendSystemMessageAsync = null,
        Func<WebSocketCloseStatus, string, Task>? closeConnectionAsync = null,
        Action<Task>? observeExecution = null)
    {
        return new ExecutionCommandContext
        {
            AgentId = Guid.NewGuid(),
            AgentSession = null,
            CancellationToken = CancellationToken.None,
            CurrentUser = "tester",
            ConnectionState = state ?? new ExecutionConnectionState(),
            ExecutionCoordinator = coordinator ?? new FakeAgentExecutionCoordinator(),
            WebSocket = new FakeWebSocket(),
            SendLock = new SemaphoreSlim(1, 1),
            SendErrorAsync = sendErrorAsync ?? (_ => Task.CompletedTask),
            SendSystemMessageAsync = sendSystemMessageAsync ?? (_ => Task.CompletedTask),
            CloseConnectionAsync = closeConnectionAsync ?? ((_, _) => Task.CompletedTask),
            ExtractReason = result => result is ObjectResult objectResult && objectResult.Value is string message
                ? message
                : "Invalid request payload",
            ObserveExecution = observeExecution ?? (_ => { })
        };
    }

    private static ExecCommand CreateExecCommand()
    {
        return new ExecCommand(
            AgentRuntimeType.Agent,
            new AgwUserInput
            {
                Contents = [new AgwTextContent { Content = "hello" }]
            });
    }

    private sealed class UnknownCommand : AgentRunCommand;

    private sealed class FakeAgentExecutionCoordinator : IAgentExecutionCoordinator
    {
        public bool NormalizeCalled { get; private set; }

        public bool ResolveCalled { get; private set; }

        public Func<SettingCommand, Task<SettingCommand>> NormalizeSettingsAsyncImpl { get; set; } =
            command => Task.FromResult(command);

        public Func<ExecutionTaskRequest, CancellationToken, Task<ExecutionTaskResolutionResult>> ResolveTaskAsyncImpl { get; set; } =
            (request, _) => Task.FromResult(new ExecutionTaskResolutionResult(
                new ProjectTask
                {
                    Id = request.TaskId ?? Guid.NewGuid(),
                    ProjectId = request.ProjectId ?? Guid.NewGuid(),
                    ContextId = "ctx-1"
                },
                null));

        public Func<StreamingExecutionStartRequest, CancellationToken, Task<ExecutionStartResult>> StartStreamingExecutionAsyncImpl { get; set; } =
            (_, _) => Task.FromResult(new ExecutionStartResult(null, null));

        public async Task<SettingCommand> NormalizeSettingsAsync(SettingCommand settings, CancellationToken cancellationToken)
        {
            NormalizeCalled = true;
            return await NormalizeSettingsAsyncImpl(settings);
        }

        public async Task<ExecutionTaskResolutionResult> ResolveTaskAsync(ExecutionTaskRequest request, CancellationToken cancellationToken)
        {
            ResolveCalled = true;
            return await ResolveTaskAsyncImpl(request, cancellationToken);
        }

        public Task<ExecutionStartResult> StartStreamingExecutionAsync(StreamingExecutionStartRequest request, CancellationToken cancellationToken) =>
            StartStreamingExecutionAsyncImpl(request, cancellationToken);
    }

    private sealed class FakeWebSocket : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string SubProtocol => string.Empty;

        public override void Abort()
        { }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Dispose()
        { }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => throw new NotSupportedException();

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
