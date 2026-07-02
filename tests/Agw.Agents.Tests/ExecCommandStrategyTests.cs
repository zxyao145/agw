using System.Net.WebSockets;

using Agw.Agents.Application.AgentRun;
using Agw.Agents.Application.AgentRun.Dtos;
using Agw.Agents.Application.Execution;
using Agw.Agents.Application.Execution.CommandStrategies;
using Agw.Agents.Contracts;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Contracts.Storage;
using Agw.Shared.Contracts.Tasks;

using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public class ExecCommandStrategyTests
{
    [Fact]
    public async Task ExecuteAsync_ResolvesNewTaskWithUserInputText()
    {
        var taskService = new CapturingTaskAppService();
        var strategy = new ExecCommandStrategy(
            NullLogger<ExecCommandStrategy>.Instance,
            taskService,
            new UnusedAgentRuntimeService(),
            null!,
            new UnusedFileSystemResolver());
        var projectId = Guid.NewGuid();
        var contextId = Guid.NewGuid().ToString("D");
        var context = new ExecutionCommandContext(
            Guid.NewGuid(),
            "tester",
            TestContext.Current.CancellationToken,
            new StubWebSocket(),
            new SemaphoreSlim(1, 1))
        {
            CloseConnectionAsync = (_, _) => Task.CompletedTask,
            ObserveTurn = _ => { }
        };
        context.ConnectionState.ApplySettings(new SettingCommand(projectId, contextId: contextId));

        await strategy.ExecuteAsync(
            new ExecCommand(
                AgentRuntimeType.Agent,
                new AgwUserInput
                {
                    Contents =
                    [
                        new AgwTextContent { Content = "Write release notes" }
                    ]
                }),
            context);

        Assert.NotNull(taskService.LastRequest);
        Assert.Equal("Write release notes", taskService.LastRequest!.Input);
        Assert.Null(taskService.LastRequest.TaskId);
        Assert.Equal(contextId, taskService.LastRequest.ContextId);
    }

    private sealed class CapturingTaskAppService : ITaskAppService
    {
        public ExecutionTaskRequest? LastRequest { get; private set; }

        public Task<TaskProjection?> GetTaskAsync(Guid value) => Task.FromResult<TaskProjection?>(null);

        public Task<TaskProjection?> CreateTaskForExecutionAsync(
            Guid projectId,
            Guid? taskId,
            string input,
            string user,
            string? contextId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<TaskProjection?>(null);

        public Task<bool> HasTaskAsync(
            Guid taskId,
            Guid? projectId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<ExecutionTaskResolutionResult> ResolveTaskAsync(
            ExecutionTaskRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new ExecutionTaskResolutionResult(
                null,
                new BadRequestObjectResult("stop")));
        }
    }

    private sealed class UnusedAgentRuntimeService : IAgentRuntimeService
    {
        public Task<AIAgent?> CreateAiAgentAsync(
            Guid agentId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AIAgent?> CreateAiAgentAsync(
            Guid agentId,
            Guid? projectId,
            Guid? taskId,
            bool resume,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentExecSession?> CreateSessionAsync(
            Guid agentId,
            TaskProjection task,
            SettingCommand settings,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
            AgentExecSession session,
            AgwUserInput input,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentExecutionResult?> ExecuteByNameAsync(
            AgentExecuteByNameRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentExecutionResult?> ExecuteByIdAsync(
            AgentExecuteByIdRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedFileSystemResolver : IAgwFileSystemResolver
    {
        public Task<IAgwFileSystem> ResolveAsync(Guid projectId, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class StubWebSocket : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => WebSocketState.Open;

        public override string? SubProtocol => null;

        public override void Abort()
        {
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override void Dispose()
        {
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
