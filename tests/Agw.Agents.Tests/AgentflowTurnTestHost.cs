using System.Runtime.CompilerServices;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Agentflows.Runtime;
using Agw.Agents.Execution.Agentflows.Turns;
using Agw.Agents.Execution.Agentflows.Workflows;
using Agw.Agents.Execution.Context;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.HumanInteraction.InProcess;
using Agw.Agents.Execution.Outbound;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Runtimes.Durable.Contracts;
using Agw.Agents.Execution.Turns;
using Agw.Shared.Runtime;

namespace Agw.Agents.Tests;

/// <summary>
/// 用真实的 AgentflowRuntimeFactory 与 AgentflowTurnExecutor 执行 Agentflow Turn；作用域与决定来源按进程内协调器的方式建立，用户沿用当前身份。
/// Runs Agentflow turns with the real AgentflowRuntimeFactory and AgentflowTurnExecutor; the scope and decision source follow the in-process coordinator and the user comes from the current identity.
/// </summary>
internal sealed class AgentflowTurnTestHost
{
    public AgentflowTurnTestHost(AgentflowRuntimeFactory factory, AgentflowTurnExecutor executor)
    {
        Factory = factory;
        Executor = executor;
    }

    public AgentflowRuntimeFactory Factory { get; }

    public AgentflowTurnExecutor Executor { get; }

    /// <summary>
    /// 最近一次 Turn 的执行作用域，用于读取 TurnExecutor 报告的结局。
    /// The execution scope of the most recent turn, used to read the outcome reported by the TurnExecutor.
    /// </summary>
    public ExecutionScope? LastScope { get; private set; }

    /// <summary>
    /// 执行一个交互式 Turn；决定来源为空时交互请求报告为不可用。
    /// Runs one interactive turn; with no decision source, interaction requests are reported as unavailable.
    /// </summary>
    public async IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        Guid agentflowId,
        string input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        Guid? projectId = null,
        string? contextId = null,
        Guid? taskId = null,
        IInteractionHandler? interactionHandler = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        Guid? conversationId = null
    )
    {
        var task = CreateTask(projectId, contextId, taskId, conversationId);
        await using var runtime = AgentflowRuntimeFactory.CreateRuntime(
            agentflowId,
            task,
            new ExecutionSettings(task.ProjectId, task.ProjectConversationId, environmentVariables),
            deferHumanInteractions: false
        );
        await foreach (
            var message in RunAsync(
                runtime,
                new TurnInput(AgentflowRuntimeFactory.CreateUserInput(input)),
                interactionHandler,
                cancellationToken
            )
        )
        {
            yield return message;
        }
    }

    /// <summary>
    /// 按无人值守规则执行一个 Turn 并收集消息流：工具审批按权限自动决定，其余人工请求使执行失败。
    /// Runs one turn under the unattended rules and collects the message stream: tool approvals follow the permission mode and other human requests fail the execution.
    /// </summary>
    public async Task<IReadOnlyList<AgwMessage>> ExecuteAsync(
        Guid agentflowId,
        Guid taskId,
        string input,
        CancellationToken cancellationToken = default,
        Guid? projectId = null,
        string? contextId = null
    )
    {
        var task = CreateTask(projectId, contextId, taskId, conversationId: null);
        await using var runtime = AgentflowRuntimeFactory.CreateRuntime(
            agentflowId,
            task,
            new ExecutionSettings(task.ProjectId, task.ProjectConversationId),
            deferHumanInteractions: true
        );
        var messages = new List<AgwMessage>();
        await foreach (
            var message in RunAsync(
                runtime,
                new TurnInput(AgentflowRuntimeFactory.CreateUserInput(input)),
                new UnattendedInteractionHandler(),
                cancellationToken
            )
        )
        {
            messages.Add(message);
        }
        return messages;
    }

    public async IAsyncEnumerable<AgwMessage> RunAsync(
        AgentflowRuntime runtime,
        TurnInput input,
        IInteractionHandler? interactionHandler,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var parent = ExecutionScope.Current;
        var context = new AgentExecutionContext
        {
            UserId = UserInfoUtil.RequiredUserId,
            ProjectId = runtime.Task.ProjectId,
            ProjectConversationId = runtime.Task.ProjectConversationId,
            ContextId = runtime.Task.ContextId,
            Generation = runtime.Task.Generation,
            WorkspaceSnapshot =
                parent?.WorkspaceSnapshot ?? new ProjectWorkspaceSnapshot("/workspace", [], "fingerprint"),
            TurnId = Guid.CreateVersion7(),
            TurnTargetId = runtime.AgentflowId,
            RuntimeType = AgentRuntimeType.Agentflow,
            PermissionMode = parent?.Context.PermissionMode,
            PermissionVersion = parent?.Context.PermissionVersion ?? 0,
            Provider = ExecutionProvider.InProcess,
        };
        var scope = ExecutionScope.Create(
            context,
            runtime.Task.TaskId,
            new InteractionTestSink(),
            new InMemoryPendingInteractionSet(context.PermissionMode)
        );
        scope.BindInteractions(
            interactionHandler,
            interactionHandler as IHumanInteractionChannel,
            interactionHandler?.Requests
        );
        LastScope = scope;
        await foreach (var message in scope.RunStreaming(Executor.RunAsync(scope, runtime, input, cancellationToken)))
        {
            yield return message;
        }
    }

    public Task<DurableExecutionSegmentResult> ExecuteDurableSegmentInScopeAsync(
        DurableExecutionManifest manifest,
        DurableExecutionSegmentInput input,
        IExecutionMessageSink sink,
        CancellationToken cancellationToken
    ) => Executor.ExecuteDurableSegmentInScopeAsync(manifest, input, sink, cancellationToken);

    public Task<string?> GetMermaidAsync(Guid agentflowId, CancellationToken cancellationToken = default) =>
        Factory.GetMermaidAsync(agentflowId, cancellationToken);

    public Task<AgentflowWorkflowLease?> CreateAiWorkflow(
        Guid agentflowId,
        CancellationToken cancellationToken = default
    ) => Factory.CreateAiWorkflow(agentflowId, cancellationToken);

    private static AgentExecutionTask CreateTask(
        Guid? projectId,
        string? contextId,
        Guid? taskId,
        Guid? conversationId
    ) =>
        new()
        {
            TaskId = taskId ?? Guid.CreateVersion7(),
            ProjectId = projectId ?? Guid.Empty,
            ProjectConversationId = conversationId ?? Guid.Empty,
            ContextId = ContextIdUtil.ResolveContextId(contextId),
            Generation = ExecutionScope.Current?.Generation ?? 0,
        };
}
