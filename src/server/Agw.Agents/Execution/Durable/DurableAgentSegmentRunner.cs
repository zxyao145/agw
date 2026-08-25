using System.Runtime.ExceptionServices;
using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Agents;
using Agw.Agents.Execution.Messaging;
using Agw.Agents.Execution.Turns;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Durable;

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
                manifest.Settings.ToCommand(manifest.Task.ProjectId, manifest.Task.ContextId),
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

        var approvalHandler = new CaptureDurableApprovalHandler();
        using var interactionScope = _humanInteractionContextAccessor.Push(
            new ResolvedHumanInteractionChannel(input.ResolvedInteractions)
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
                        CreateApprovalResponseMessage(input.ResolvedInteractions),
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
                Status = DurableExecutionSegmentStatus.Completed,
            };
        }
        // CaptureDurableApprovalHandler 以异常立即截断本次模型调用，确保先把 pending 快照原子写入 PostgreSQL。
        catch (AgwException) when (approvalHandler.PendingInteraction is { } interaction)
        {
            return new DurableExecutionSegmentResult
            {
                ExecutionId = manifest.ExecutionId,
                SegmentIndex = input.SegmentIndex,
                Status = DurableExecutionSegmentStatus.WaitingForHuman,
                PendingInteractions = [interaction],
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
        IReadOnlyList<DurableResolvedInteraction> resolvedInteractions
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
            if (string.IsNullOrWhiteSpace(request.ToolName) || string.IsNullOrWhiteSpace(request.CallId))
            {
                throw new AgwException(
                    ErrorCodes.DurableExecutionConflict,
                    $"Human response '{request.RequestId}' does not contain a Tool call snapshot."
                );
            }

            var argumentPayload = request.Arguments ?? request.Payload;
            var arguments = argumentPayload.HasValue
                ? JsonUtil.Deserialize<Dictionary<string, object?>>(argumentPayload.Value.GetRawText())
                : null;
            var toolCall = new FunctionCallContent(request.CallId, request.ToolName, arguments);
            var approval = new ToolApprovalRequestContent(request.RequestId, toolCall);
            var response = resolved.Response;
            contents.Add(
                ToolApprovalSupport.CreateResponse(
                    approval,
                    new HumanGateApprovalDecision(
                        response.RequestId,
                        response.Approved,
                        response.ResponseText,
                        response.ApprovalScope,
                        response.ResponseData
                    )
                )
            );
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

    /// <summary>
    /// 捕获首次人工审批请求并立即结束当前 durable segment。
    /// </summary>
    private sealed class CaptureDurableApprovalHandler : IHumanGateApprovalHandler
    {
        /// <summary>
        /// 获取本分段首次捕获、需要写入 PostgreSQL 持久等待的人工请求。
        /// </summary>
        public DurableHumanInteractionSnapshot? PendingInteraction { get; private set; }

        /// <summary>
        /// 保存待处理请求；durable 模式不会在当前 segment 的进程内等待用户回答。
        /// </summary>
        public ValueTask<HumanGateApprovalDecision> WaitForApprovalAsync(
            HumanGateApprovalRequest request,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            PendingInteraction = DurableHumanInteractionMapper.FromRequest(request);
            throw new AgwException(
                ErrorCodes.DurableExecutionConflict,
                $"Human interaction '{PendingInteraction.RequestId}' is pending."
            );
        }
    }
}
