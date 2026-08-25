using System.Runtime.CompilerServices;
using System.Security.Claims;
using Agw.Agents.Contracts.Execution;
using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Agents;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Durable;
using Agw.Agents.Execution.Facades;
using Agw.Agents.Execution.Runtimes;
using Agw.Auth.Contracts;
using Agw.Projects.Contracts.Execution;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using AgentsDtos = Agw.Agents.Execution.Agents.Dtos;

namespace Agw.Agents.Tests;

public sealed class AgentExecutionFacadeTests
{
    [Fact]
    public async Task ExecuteAsync_Distributed_WaitsForOutcomeWithoutReadingEventStream()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var client = new RecordingDurableExecutionClient();
        await using var services = new ServiceCollection()
            .AddSingleton<IDurableExecutionClient>(client)
            .BuildServiceProvider();
        var facade = new AgentExecutionFacade(
            agentRuntimeService: null!,
            agentflowRuntimeService: null!,
            catalog: null!,
            services,
            Options.Create(new ExecutionRuntimeOptions { Provider = ExecutionProvider.Distributed })
        );
        var executionId = Guid.CreateVersion7();
        var request = new AgentExecutionRequest(
            executionId,
            "owner",
            new AgentTarget(AgentTargetKind.Agent, Guid.CreateVersion7()),
            new ProjectTaskSnapshot(
                executionId,
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                "context",
                null,
                "Task",
                ProjectTaskStatus.Running,
                null,
                DateTimeOffset.UtcNow,
                null,
                null
            ),
            new AgwUserInput
            {
                MessageId = executionId.ToString("D"),
                Author = "user",
                Contents = [new AgwTextContent { Content = "run" }],
            },
            HumanInteractionPolicy: HumanInteractionPolicy.Reject
        );

        var result = await facade.ExecuteAsync(request, cancellationToken);

        Assert.Equal(AgentExecutionState.Completed, result.State);
        Assert.Equal(1, client.StartCount);
        Assert.Equal(1, client.WaitCount);
        Assert.Equal(0, client.ReadCount);
    }

    [Fact]
    public async Task ExecuteAsync_InProcess_RestoresUserContext()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var runtime = new RecordingAgentRuntimeService();
        await using var services = new ServiceCollection().BuildServiceProvider();
        var facade = new AgentExecutionFacade(
            runtime,
            agentflowRuntimeService: null!,
            catalog: null!,
            services,
            Options.Create(new ExecutionRuntimeOptions { Provider = ExecutionProvider.InProcess })
        );
        var executionId = Guid.CreateVersion7();
        var previousUser = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "caller")], "Test")
        );
        UserInfoUtil.Current = previousUser;
        try
        {
            var request = new AgentExecutionRequest(
                executionId,
                "owner",
                new AgentTarget(AgentTargetKind.Agent, Guid.CreateVersion7()),
                CreateTaskSnapshot(executionId),
                new AgwUserInput
                {
                    MessageId = executionId.ToString("D"),
                    Author = "user",
                    Contents = [new AgwTextContent { Content = "run" }],
                }
            );

            await facade.ExecuteAsync(request, cancellationToken);

            Assert.Equal("owner", runtime.CapturedUserId);
            Assert.Same(previousUser, UserInfoUtil.Current);
        }
        finally
        {
            UserInfoUtil.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteStreamingAsync_InProcess_RejectsHumanInteraction()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var agentflowRuntime = new RejectingAgentflowRuntimeService();
        await using var services = new ServiceCollection().BuildServiceProvider();
        var facade = new AgentExecutionFacade(
            agentRuntimeService: null!,
            agentflowRuntime,
            catalog: null!,
            services,
            Options.Create(new ExecutionRuntimeOptions { Provider = ExecutionProvider.InProcess })
        );
        var executionId = Guid.CreateVersion7();
        var request = new AgentExecutionRequest(
            executionId,
            "owner",
            new AgentTarget(AgentTargetKind.Agentflow, Guid.CreateVersion7()),
            CreateTaskSnapshot(executionId),
            new AgwUserInput
            {
                MessageId = executionId.ToString("D"),
                Author = "user",
                Contents = [new AgwTextContent { Content = "run" }],
            },
            HumanInteractionPolicy: HumanInteractionPolicy.Reject
        );

        var exception = await Assert.ThrowsAsync<AgwException>(async () =>
        {
            await foreach (var _ in facade.ExecuteStreamingAsync(request, cancellationToken)) { }
        });

        Assert.Equal(ErrorCodes.AgentExecutionFailed.Code, exception.Code);
    }

    private static ProjectTaskSnapshot CreateTaskSnapshot(Guid executionId) =>
        new(
            executionId,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "context",
            null,
            "Task",
            ProjectTaskStatus.Running,
            null,
            DateTimeOffset.UtcNow,
            null,
            null
        );

    private sealed class RecordingAgentRuntimeService : IAgentRuntimeService
    {
        public string? CapturedUserId { get; private set; }

        public Task<AIAgent?> CreateAiAgentAsync(Guid agentId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AIAgent?>(null);

        public Task<AIAgent?> CreateAiAgentAsync(
            Guid agentId,
            Guid? projectId,
            bool resume,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<AIAgent?>(null);

        public Task<AIAgent?> CreateAiAgentAsync(
            Guid agentId,
            Guid? projectId,
            bool resume,
            IReadOnlyDictionary<string, string>? environmentVariables,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<AIAgent?>(null);

        public Task<AgentRuntime?> CreateRuntimeAsync(
            Guid agentId,
            TaskProjection task,
            SettingCommand settings,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<AgentRuntime?>(null);

        public async IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
            AgentRuntime session,
            AgwUserInput input,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IReadOnlyList<AgwMessage>> ExecuteAsync(
            AgentRuntime session,
            AgwUserInput input,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyList<AgwMessage>>([]);

        public Task<AgentsDtos.AgentExecutionResult?> ExecuteByIdAsync(
            AgentsDtos.AgentExecuteByIdRequest request,
            CancellationToken cancellationToken = default
        )
        {
            CapturedUserId = UserInfoUtil.UserId;
            var taskId = request.TaskId?.ToString("D") ?? string.Empty;
            return Task.FromResult<AgentsDtos.AgentExecutionResult?>(new AgentsDtos.AgentExecutionResult(taskId, []));
        }
    }

    private sealed class RejectingAgentflowRuntimeService : IAgentflowRuntimeService
    {
        public async IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
            Guid agentflowId,
            string input,
            CancellationToken cancellationToken = default,
            Guid? projectId = null,
            string? contextId = null,
            Guid? taskId = null,
            IHumanGateApprovalHandler? humanGateApprovalHandler = null,
            IReadOnlyDictionary<string, string>? environmentVariables = null,
            Guid? conversationId = null,
            PermissionMode? permissionMode = null
        )
        {
            await Task.CompletedTask;
            yield return new AgwMessage(
                "m1",
                "agent",
                AiRole.Assistant,
                [],
                new Dictionary<string, object?> { ["type"] = "human-interaction-request" }
            );
        }

        public Task<AgentflowExecutionResult?> ExecuteAsync(
            Guid agentflowId,
            Guid taskId,
            string input,
            CancellationToken cancellationToken = default,
            Guid? projectId = null,
            string? contextId = null
        ) => Task.FromResult<AgentflowExecutionResult?>(null);

        public Task<AgentflowExecutionResult?> ExecuteAsync(
            Guid agentflowId,
            Guid taskId,
            List<ChatMessage> messages,
            CancellationToken cancellationToken = default,
            Guid? projectId = null,
            string? contextId = null
        ) => Task.FromResult<AgentflowExecutionResult?>(null);

        public Task<AgentflowWorkflowLease?> CreateAiWorkflow(
            Guid agentflowId,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<AgentflowWorkflowLease?>(null);

        public Task<string?> GetMermaidAsync(Guid agentflowId, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    private sealed class RecordingDurableExecutionClient : IDurableExecutionClient
    {
        public int StartCount { get; private set; }
        public int WaitCount { get; private set; }
        public int ReadCount { get; private set; }

        public Task StartAsync(DurableExecutionRequest request, CancellationToken cancellationToken)
        {
            StartCount++;
            return Task.CompletedTask;
        }

        public Task<DurableExecutionOutcome> GetOutcomeAsync(
            Guid executionId,
            string userId,
            CancellationToken cancellationToken
        ) => Task.FromResult(new DurableExecutionOutcome(executionId, DurableExecutionStatus.Completed, null));

        public Task<DurableExecutionOutcome> WaitForActionableOutcomeAsync(
            Guid executionId,
            string userId,
            CancellationToken cancellationToken
        )
        {
            WaitCount++;
            return Task.FromResult(new DurableExecutionOutcome(executionId, DurableExecutionStatus.Completed, null));
        }

        public async IAsyncEnumerable<DurableExecutionEvent> ReadAsync(
            Guid executionId,
            string userId,
            string? afterCursor,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            ReadCount++;
            await Task.CompletedTask;
            yield break;
        }

        public Task<bool> InterruptAsync(
            Guid executionId,
            string userId,
            string? reason,
            CancellationToken cancellationToken
        ) => Task.FromResult(true);
    }
}
