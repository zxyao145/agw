using System.Runtime.CompilerServices;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Agentflows.Checkpoints;
using Agw.Agents.Execution.Agentflows.Context;
using Agw.Agents.Execution.Agentflows.Observability;
using Agw.Agents.Execution.Agentflows.Runners.Durable;
using Agw.Agents.Execution.Agentflows.Runners.InProcess;
using Agw.Agents.Execution.Agentflows.Workflows;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;
using Agw.Agents.Execution.Outbound;
using Agw.Agents.Execution.Runtimes.Durable.Contracts;
using Agw.Agents.Execution.Turns;
using Agw.Projects.Contracts.Runtime;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.AI;
using static Agw.Agents.Execution.Agentflows.Messaging.AgentflowMessageMapper;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agw.Agents.Execution.Agentflows.Runtime;

public record AgentflowExecutionResult(string TaskId, string ContextId, IReadOnlyList<AgwMessage> Messages)
{
    public AgentflowExecutionResult(string taskId, IReadOnlyList<AgwMessage> messages)
        : this(taskId, taskId, messages) { }
}

public class AgentflowRuntimeService : IAgentflowRuntimeService
{
    private readonly IProjectDefaultResolver _projectDefaults;
    private readonly IProjectRuntimeFacade _projectRuntimeFacade;
    private readonly AgentflowWorkflowFactory _workflowFactory;
    private readonly AgentflowExecutionContextFactory _executionContextFactory;
    private readonly DurableAgentflowSegmentRunner _durableRunner;
    private readonly InProcessAgentflowRunner _inProcessRunner;
    private readonly IConversationHistoryPersistence? _historyPersistence;

    public AgentflowRuntimeService(
        AgentflowWorkflowFactory workflowFactory,
        AgentflowExecutionContextFactory executionContextFactory,
        InProcessAgentflowRunner inProcessRunner,
        DurableAgentflowSegmentRunner durableRunner,
        IProjectDefaultResolver projectDefaults,
        IProjectRuntimeFacade projectRuntimeFacade,
        IConversationHistoryPersistence? historyPersistence = null
    )
    {
        _historyPersistence = historyPersistence;
        _workflowFactory = workflowFactory;
        _executionContextFactory = executionContextFactory;
        _inProcessRunner = inProcessRunner;
        _durableRunner = durableRunner;
        _projectDefaults = projectDefaults;
        _projectRuntimeFacade = projectRuntimeFacade;
    }

    /// <summary>
    /// 为指定 Agentflow 创建或恢复 context，并以流式消息执行工作流和处理人工审批。
    /// </summary>
    public IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        Guid agentflowId,
        string input,
        CancellationToken cancellationToken = default,
        Guid? projectId = null,
        string? contextId = null,
        Guid? taskId = null,
        IInteractionHandler? interactionHandler = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        Guid? conversationId = null,
        PermissionMode? permissionMode = null
    ) =>
        ExecuteStreamingCoreAsync(
            agentflowId,
            AgentflowExecutionContextFactory.CreateUserInput(input),
            cancellationToken,
            projectId,
            contextId,
            taskId,
            interactionHandler,
            environmentVariables,
            conversationId,
            new MafPermissionState(permissionMode),
            sourceExecutionId: null,
            checkpointState: null,
            resumeCheckpoint: null
        );

    internal IAsyncEnumerable<AgwMessage> ExecuteStreamingWithPermissionStateAsync(
        Guid agentflowId,
        AgwUserInput input,
        CancellationToken cancellationToken,
        Guid? projectId,
        string? contextId,
        Guid? taskId,
        IInteractionHandler? interactionHandler,
        IReadOnlyDictionary<string, string>? environmentVariables,
        Guid? conversationId,
        MafPermissionState permissionState,
        Guid? sourceExecutionId,
        AgentflowCheckpointRuntimeState checkpointState,
        AgentflowCheckpointSnapshot? resumeCheckpoint
    ) =>
        ExecuteStreamingCoreAsync(
            agentflowId,
            input,
            cancellationToken,
            projectId,
            contextId,
            taskId,
            interactionHandler,
            environmentVariables,
            conversationId,
            permissionState,
            sourceExecutionId,
            checkpointState,
            resumeCheckpoint
        );

    private async IAsyncEnumerable<AgwMessage> ExecuteStreamingCoreAsync(
        Guid agentflowId,
        AgwUserInput input,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Guid? projectId,
        string? contextId,
        Guid? taskId,
        IInteractionHandler? interactionHandler,
        IReadOnlyDictionary<string, string>? environmentVariables,
        Guid? conversationId,
        MafPermissionState permissionState,
        Guid? sourceExecutionId,
        AgentflowCheckpointRuntimeState? checkpointState,
        AgentflowCheckpointSnapshot? resumeCheckpoint
    )
    {
        var agentflow = await _workflowFactory.GetVisibleAgentflowAsync(agentflowId);
        if (agentflow == null)
        {
            throw new AgwException(ErrorCodes.ResourceNotFound);
        }

        var resolvedProjectId = await ResolveProjectIdAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (!resolvedProjectId.HasValue)
        {
            throw new AgwException(ErrorCodes.ResourceNotFound);
        }
        if (!await IsProjectVisibleAsync(resolvedProjectId.Value, cancellationToken).ConfigureAwait(false))
        {
            throw new AgwException(ErrorCodes.ResourceNotFound);
        }
        var executionUserId = _workflowFactory.ResolveExecutionUserId();
        var resolvedContextId = ContextIdUtil.ResolveContextId(contextId);
        var resolvedTaskId = taskId ?? Guid.CreateVersion7();
        var executionTraceContext = new AgentflowExecutionTraceContext(
            resolvedProjectId.Value,
            resolvedContextId,
            resolvedTaskId
        );
        var sessionScope = await _executionContextFactory
            .CreateSessionScopeAsync(
                resolvedProjectId.Value,
                resolvedContextId,
                resolvedTaskId,
                conversationId,
                cancellationToken,
                permissionState
            )
            .ConfigureAwait(false);
        await foreach (
            var message in ConversationHistoryPersistenceContext.RunStreaming(
                ExecutePreparedAsync(cancellationToken),
                _historyPersistence,
                resolvedProjectId.Value,
                resolvedContextId,
                ConversationSessionContext.GetGeneration(resolvedProjectId.Value, resolvedContextId),
                allowCreateConversation: sessionScope.ConversationId == Guid.Empty
            )
        )
        {
            yield return message;
        }

        async IAsyncEnumerable<AgwMessage> ExecutePreparedAsync(
            [EnumeratorCancellation] CancellationToken executionToken
        )
        {
            var workflowLease = await _workflowFactory.CreateAiWorkflow(
                agentflow,
                executionToken,
                sessionScope,
                executionTraceContext,
                environmentVariables
            );
            if (workflowLease == null)
            {
                yield break;
            }

            AgwMessage? finished = null;
            {
                await using var workflowResources = workflowLease;
                await foreach (
                    var message in ConversationHistoryPersistenceContext
                        .ObserveAsync(
                            _inProcessRunner.ExecuteStreamingAsync(
                                agentflow.Id,
                                input,
                                sessionScope,
                                executionTraceContext,
                                workflowLease,
                                interactionHandler,
                                executionUserId,
                                sourceExecutionId,
                                checkpointState,
                                resumeCheckpoint,
                                executionToken
                            ),
                            executionToken
                        )
                        .ConfigureAwait(false)
                )
                {
                    if (TurnMessageProtocol.IsFinished(message))
                        finished = message;
                    else
                        yield return message;
                }
            }
            await ConversationHistoryPersistenceContext.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            if (finished != null)
                yield return finished;
        }
    }

    /// <summary>
    /// 执行或恢复一个 Agentflow durable 分段，并把 pending 请求与最新 checkpoint 返回给 PostgreSQL 状态机。
    /// </summary>
    internal async Task<DurableExecutionSegmentResult> ExecuteDurableSegmentAsync(
        DurableExecutionManifest manifest,
        DurableExecutionSegmentInput input,
        IExecutionMessageSink sink,
        CancellationToken cancellationToken
    )
    {
        if (!_durableRunner.IsAvailable)
        {
            return CreateDurableFailure(input, "Human interaction context is unavailable.");
        }

        var agentflow = await _workflowFactory.GetVisibleAgentflowAsync(manifest.AgentId);
        if (agentflow == null)
        {
            return CreateDurableFailure(input, "Agentflow could not be found.");
        }

        var resolvedProjectId = await ResolveProjectIdAsync(manifest.Task.ProjectId, cancellationToken)
            .ConfigureAwait(false);
        if (!resolvedProjectId.HasValue)
        {
            return CreateDurableFailure(input, "The default project was not found.");
        }
        if (!await IsProjectVisibleAsync(resolvedProjectId.Value, cancellationToken).ConfigureAwait(false))
        {
            return CreateDurableFailure(input, "The project was not found.");
        }
        var resolvedContextId = ContextIdUtil.ResolveContextId(manifest.Task.ContextId);
        var executionTraceContext = new AgentflowExecutionTraceContext(
            resolvedProjectId.Value,
            resolvedContextId,
            manifest.Task.TaskId
        );
        var sessionScope = await _executionContextFactory
            .CreateSessionScopeAsync(
                resolvedProjectId.Value,
                resolvedContextId,
                manifest.Task.TaskId,
                manifest.Task.ProjectConversationId,
                cancellationToken,
                _durableRunner.CreatePermissionState(manifest)
            )
            .ConfigureAwait(false);
        var workflowLease = await _workflowFactory.CreateAiWorkflow(
            agentflow,
            cancellationToken,
            sessionScope,
            executionTraceContext,
            manifest.Settings.EnvironmentVariables,
            deferHumanInteractions: true
        );
        if (workflowLease == null)
        {
            return CreateDurableFailure(input, "Agentflow could not be constructed.");
        }

        await using var workflowResources = workflowLease;
        return await _durableRunner
            .RunAsync(manifest, input, sink, sessionScope, workflowLease, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AgentflowExecutionResult?> ExecuteAsync(
        Guid agentflowId,
        Guid taskId,
        string input,
        CancellationToken cancellationToken = default,
        Guid? projectId = null,
        string? contextId = null,
        PermissionMode? permissionMode = null
    )
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, input) { AuthorName = Constants.DefaultInputAuthor },
        };

        return await ExecuteAsync(
                agentflowId,
                taskId,
                messages,
                cancellationToken,
                projectId,
                contextId,
                permissionMode
            )
            .ConfigureAwait(false);
    }

    public async Task<AgentflowExecutionResult?> ExecuteAsync(
        Guid agentflowId,
        Guid taskId,
        List<ChatMessage> messages,
        CancellationToken cancellationToken = default,
        Guid? projectId = null,
        string? contextId = null,
        PermissionMode? permissionMode = null
    )
    {
        var resolvedProjectId = await ResolveProjectIdAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (!resolvedProjectId.HasValue)
        {
            return null;
        }
        if (!await IsProjectVisibleAsync(resolvedProjectId.Value, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return await ExecuteAsync(
                agentflowId,
                resolvedProjectId.Value,
                taskId,
                messages,
                cancellationToken,
                contextId,
                permissionMode
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 使用已转换的聊天消息执行 Agentflow，并返回归一化 context 下的完整执行结果。
    /// </summary>
    private async Task<AgentflowExecutionResult?> ExecuteAsync(
        Guid agentflowId,
        Guid projectId,
        Guid? taskId,
        List<ChatMessage> messages,
        CancellationToken cancellationToken,
        string? contextId = null,
        PermissionMode? permissionMode = null
    )
    {
        if (!await IsProjectVisibleAsync(projectId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var agentflow = await _workflowFactory.GetVisibleAgentflowAsync(agentflowId);
        if (agentflow == null)
        {
            return null;
        }

        if (taskId == null)
        {
            taskId = Guid.CreateVersion7();
        }

        var resolvedContextId = ContextIdUtil.ResolveContextId(contextId);
        var executionTraceContext = new AgentflowExecutionTraceContext(projectId, resolvedContextId, taskId.Value);
        var sessionScope = await _executionContextFactory
            .CreateSessionScopeAsync(
                projectId,
                resolvedContextId,
                taskId,
                conversationId: null,
                cancellationToken,
                new MafPermissionState(permissionMode)
            )
            .ConfigureAwait(false);
        await using var historyScope = ConversationHistoryPersistenceContext.BeginScope(
            _historyPersistence,
            projectId,
            resolvedContextId,
            ConversationSessionContext.GetGeneration(projectId, resolvedContextId),
            allowCreateConversation: sessionScope.ConversationId == Guid.Empty
        );
        var workflowLease = await _workflowFactory.CreateAiWorkflow(
            agentflow,
            cancellationToken,
            sessionScope,
            executionTraceContext,
            deferHumanInteractions: true
        );
        if (workflowLease == null)
        {
            return null;
        }

        await using var workflowResources = workflowLease;
        return await ConversationHistoryPersistenceContext
            .ObserveAsync(
                _inProcessRunner.ExecuteAsync(
                    agentflow.Id,
                    taskId.Value,
                    resolvedContextId,
                    workflowLease,
                    messages,
                    cancellationToken,
                    new UnattendedInteractionHandler(permissionMode)
                )
            )
            .ConfigureAwait(false);
    }

    private async Task<Guid?> ResolveProjectIdAsync(Guid? projectId, CancellationToken cancellationToken)
    {
        if (
            projectId.HasValue
            && projectId.Value != Guid.Empty
            && projectId.Value != ProjectDefaults.DefaultBuiltInId
            && projectId.Value != ProjectDefaults.A2AId
        )
        {
            return projectId.Value;
        }

        return projectId == ProjectDefaults.A2AId
            ? await _projectDefaults.ResolveA2AProjectIdAsync(cancellationToken).ConfigureAwait(false)
            : await _projectDefaults.ResolveDefaultProjectIdAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> IsProjectVisibleAsync(Guid projectId, CancellationToken cancellationToken)
    {
        return await _projectRuntimeFacade.GetForCurrentUserAsync(projectId, cancellationToken).ConfigureAwait(false)
            != null;
    }

    public Task<string?> GetMermaidAsync(Guid agentflowId, CancellationToken cancellationToken = default) =>
        _workflowFactory.GetMermaidAsync(agentflowId, cancellationToken);

    public Task<AgentflowWorkflowLease?> CreateAiWorkflow(
        Guid agentflowId,
        CancellationToken cancellationToken = default
    ) => _workflowFactory.CreateAiWorkflow(agentflowId, cancellationToken);
}
