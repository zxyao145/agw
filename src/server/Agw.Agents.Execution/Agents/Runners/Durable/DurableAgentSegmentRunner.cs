using System.Runtime.ExceptionServices;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Agents.Execution.HumanInteraction;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.HumanInteraction.Durable;
using Agw.Agents.Execution.HumanInteraction.Durable.Contracts;
using Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;
using Agw.Agents.Execution.Outbound;
using Agw.Agents.Execution.Persistence.Durable;
using Agw.Agents.Execution.Runtimes.Durable.Contracts;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.Runners.Durable;

/// <summary>
/// 执行 standalone System Agent 的一个可恢复分段。每个 segment 都重建 runtime，
/// 再通过既有 Agent session 和已解析的人工回答继续上一个 Tool approval 边界。
/// </summary>
internal sealed class DurableAgentSegmentRunner
{
    private readonly AgentRuntimeService _runtimeService;
    private readonly HumanInteractionContextAccessor _humanInteractionContextAccessor;

    /// <summary>
    /// 初始化 standalone Agent 分段执行器。
    /// </summary>
    public DurableAgentSegmentRunner(
        AgentRuntimeService runtimeService,
        HumanInteractionContextAccessor humanInteractionContextAccessor
    )
    {
        _runtimeService = runtimeService;
        _humanInteractionContextAccessor = humanInteractionContextAccessor;
    }

    /// <summary>
    /// 执行指定 durable 分段，并返回完成、失败或等待人工输入的持久结果。
    /// </summary>
    public async Task<DurableExecutionSegmentResult> RunAsync(
        DurableExecutionManifest manifest,
        DurableExecutionSegmentInput input,
        IExecutionMessageSink sink,
        CancellationToken cancellationToken
    )
    {
        var runtime = await _runtimeService
            .CreateDurableRuntimeAsync(
                manifest.AgentId,
                manifest.Task.ToProjection(),
                manifest.Settings.ToRuntimeSettings(manifest.Task.ProjectId, manifest.Task.ContextId),
                cancellationToken
            )
            .ConfigureAwait(false);
        if (runtime == null)
        {
            return Failure(manifest.ExecutionId, input.SegmentIndex, "Agent could not be created.");
        }
        if (runtime.AgentType == AgentType.External)
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
            return Failure(
                manifest.ExecutionId,
                input.SegmentIndex,
                "Distributed execution currently supports System Agents only."
            );
        }

        var permissions = new MafPermissionState(
            _humanInteractionContextAccessor.PermissionState
                ?? new InteractionPermissionState(
                    manifest.Settings.PermissionMode,
                    manifest.ExecutionId,
                    manifest.Settings.PermissionVersion
                )
        );
        permissions.Register(runtime.Session);
        var registry = new InteractionRequestRegistry(input.InputCatalog);
        var approvalHandler = new DurableInteractionHandler(
            permissions.Permissions,
            manifest.Settings.HumanInteractionPolicy,
            registry,
            _humanInteractionContextAccessor!.RefreshPermissionsAsync
        );
        using var interactionScope = _humanInteractionContextAccessor.Push(
            new ResolvedHumanInteractionChannel(
                input
                    .ResolvedInputs.Concat(
                        input.ResolvedInteractions.Where(item => item.Request is UserInputInteraction)
                    )
                    .DistinctBy(item => item.Request.InteractionId)
                    .ToArray()
            ),
            registry,
            permissions.Permissions
        );
        Exception? failure = null;
        try
        {
            // 首段消费原始用户输入；后续分段向已恢复的 Agent session 注入 Tool approval 响应。
            var messages =
                input.SegmentIndex == 0
                    ? _runtimeService.ExecuteStreamingAsync(runtime, manifest.Input, approvalHandler, cancellationToken)
                    : _runtimeService.ExecuteDurableSegmentStreamingAsync(
                        runtime,
                        CreateApprovalResponseMessage(input.ResolvedInteractions, permissions.Current),
                        manifest.Input,
                        approvalHandler,
                        cancellationToken
                    );
            await foreach (var message in messages.ConfigureAwait(false))
            {
                await sink.WriteAsync(message, cancellationToken).ConfigureAwait(false);
            }

            return new DurableExecutionSegmentResult
            {
                ExecutionId = manifest.ExecutionId,
                SegmentIndex = input.SegmentIndex,
                Status =
                    approvalHandler.Pending.Count == 0
                        ? DurableExecutionSegmentStatus.Completed
                        : DurableExecutionSegmentStatus.WaitingForHuman,
                PendingInteractions = approvalHandler.Pending.ToArray(),
                InputCatalog = registry.Snapshot(),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            failure = exception;
            await sink.WriteAsync(CreateFailureMessage(exception), CancellationToken.None).ConfigureAwait(false);
            return Failure(manifest.ExecutionId, input.SegmentIndex, exception.Message);
        }
        finally
        {
            try
            {
                await runtime.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception disposeException)
            {
                if (failure == null)
                {
                    ExceptionDispatchInfo.Capture(disposeException).Throw();
                }
            }
        }
    }

    /// <summary>
    /// 创建不会再抛出运行时异常的失败分段结果。
    /// </summary>
    private static DurableExecutionSegmentResult Failure(Guid executionId, int segmentIndex, string error) =>
        new()
        {
            ExecutionId = executionId,
            SegmentIndex = segmentIndex,
            Status = DurableExecutionSegmentStatus.Failed,
            ErrorMessage = error,
        };

    /// <summary>
    /// 把 PostgreSQL 中已解析的人工回答还原为 MAF Tool approval 响应消息。
    /// </summary>
    private static ChatMessage CreateApprovalResponseMessage(
        IReadOnlyList<DurableResolvedInteraction> resolvedInteractions,
        AgwPermissionMode? permissionMode
    )
    {
        if (resolvedInteractions.Count == 0)
        {
            throw new AgwException(
                ErrorCodes.DurableExecutionConflict,
                "A resumed Agent segment requires at least one persisted human response."
            );
        }

        var contents = new List<AIContent>(resolvedInteractions.Count);
        foreach (var resolved in resolvedInteractions)
        {
            var request = resolved.Request;
            var source = request.Source;
            if (
                string.IsNullOrWhiteSpace(source.ToolName)
                || string.IsNullOrWhiteSpace(source.CallId)
                || string.IsNullOrWhiteSpace(source.ProviderRequestId)
            )
                throw new AgwException(
                    ErrorCodes.DurableExecutionConflict,
                    "The interaction has no resumable tool identity."
                );
            var argumentsPayload = request switch
            {
                ToolApprovalInteraction tool => tool.Arguments,
                UserInputInteraction input => input.Arguments ?? input.Payload,
                _ => throw new AgwException(
                    ErrorCodes.DurableExecutionConflict,
                    "An Agent can only resume tool interactions."
                ),
            };
            var arguments = argumentsPayload.HasValue
                ? JsonUtil.Deserialize<Dictionary<string, object?>>(argumentsPayload.Value.GetRawText())
                : null;
            var approval = new ToolApprovalRequestContent(
                source.ProviderRequestId,
                new FunctionCallContent(source.CallId, source.ToolName, arguments)
            );
            var decision = InteractionRules.ValidateAndNormalize(request, resolved.Response, permissionMode);
            contents.Add(MafApprovalAdapter.CreateResponse(approval, decision));
        }

        return new ChatMessage(ChatRole.User, contents) { AuthorName = "human" };
    }

    /// <summary>
    /// 创建供客户端展示的 Agent 执行错误消息。
    /// </summary>
    private static AgwMessage CreateFailureMessage(Exception exception) =>
        new(
            Guid.CreateVersion7().ToString("N"),
            Constants.DefaultAgentAuthor,
            AiRole.System,
            [new AgwErrorContent { Content = exception.Message }]
        );
}
