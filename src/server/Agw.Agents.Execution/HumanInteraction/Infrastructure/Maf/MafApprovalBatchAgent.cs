using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agw.Agents.Execution.Context;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Shared.Exceptions;
using Agw.Tools.HumanInteraction;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;

/// <summary>
/// Agent 管线内的审批批次：工具审批在这里自动决定，需要用户决定的完整子集交给调用方；回答齐全前只保存部分回答，齐全后把完整的原生响应批次交回内层 Agent。
/// The approval batch inside the Agent pipeline: tool approvals are decided automatically here and the complete subset that needs a user goes to the caller; partial answers are only saved, and a complete native response batch goes back to the inner Agent once every answer arrived.
/// </summary>
/// <remarks>
/// 批次保存在实际 Session 的 StateBag 中并随 Session 序列化；模型调用与工具执行仍由 MAF 与 FunctionInvokingChatClient 完成。
/// The batch lives in the actual session's StateBag and is serialized with it; model calls and tool execution stay with MAF and FunctionInvokingChatClient.
/// </remarks>
internal sealed class MafApprovalBatchAgent : DelegatingAIAgent
{
    internal const string BatchStateKey = "agw.approvalBatch";

    private static readonly JsonSerializerOptions JsonOptions = AIJsonUtilities.DefaultOptions;

    private readonly HumanInteractionContextAccessor? _interactions;
    private readonly IReadOnlyList<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>> _autoApprovalRules;

    public MafApprovalBatchAgent(
        AIAgent innerAgent,
        HumanInteractionContextAccessor? interactions,
        IReadOnlyList<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>> autoApprovalRules
    )
        : base(innerAgent)
    {
        ArgumentNullException.ThrowIfNull(autoApprovalRules);
        _interactions = interactions;
        _autoApprovalRules = autoApprovalRules;
    }

    internal static MafApprovalBatchState? ReadBatch(AgentSession session) =>
        session.StateBag.TryGetValue<MafApprovalBatchState>(BatchStateKey, out var state, JsonOptions) ? state : null;

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        session ??= await InnerAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        var next = TakeInput(messages, session);
        if (next == null)
            return new AgentResponse(new List<ChatMessage>());

        var collected = new List<ChatMessage>();
        UsageDetails? usage = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await InnerAgent.RunAsync(next, session, options, cancellationToken).ConfigureAwait(false);
            if (response.Usage is { } responseUsage)
                (usage ??= new UsageDetails()).Add(responseUsage);
            var requests = response
                .Messages.SelectMany(message => message.Contents)
                .OfType<ToolApprovalRequestContent>()
                .ToList();
            if (requests.Count == 0)
            {
                collected.AddRange(response.Messages);
                return CreateResponse(response, collected, usage);
            }

            var items = await DecideAsync(requests, session, options, next, cancellationToken).ConfigureAwait(false);
            var automatic = items
                .Where(item => !item.RequiresHuman)
                .Select(item => item.Request.RequestId)
                .ToHashSet(StringComparer.Ordinal);
            collected.AddRange(RemoveRequests(response.Messages, automatic));
            if (automatic.Count == items.Count)
            {
                next = ContinueAutomatically(items);
                continue;
            }

            SaveBatch(session, CreateBatch(items));
            return CreateResponse(response, collected, usage);
        }
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        session ??= await InnerAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        var next = TakeInput(messages, session);
        if (next == null)
            yield break;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 审批请求收集到本次内层运行结束，其他内容继续流式输出。
            // Approval requests are collected until this inner run ends; everything else keeps streaming.
            var requests = new List<ToolApprovalRequestContent>();
            await foreach (
                var update in InnerAgent
                    .RunStreamingAsync(next, session, options, cancellationToken)
                    .ConfigureAwait(false)
            )
            {
                if (!update.Contents.Any(content => content is ToolApprovalRequestContent))
                {
                    yield return update;
                    continue;
                }

                requests.AddRange(update.Contents.OfType<ToolApprovalRequestContent>());
                var remaining = update.Contents.Where(content => content is not ToolApprovalRequestContent).ToList();
                if (remaining.Count > 0)
                    yield return CloneUpdate(update, remaining);
            }

            if (requests.Count == 0)
            {
                yield break;
            }

            var items = await DecideAsync(requests, session, options, next, cancellationToken).ConfigureAwait(false);
            if (items.All(item => !item.RequiresHuman))
            {
                next = ContinueAutomatically(items);
                continue;
            }

            SaveBatch(session, CreateBatch(items));
            var human = items
                .Where(item => item.RequiresHuman)
                .Select(item => item.Request.RequestId)
                .ToHashSet(StringComparer.Ordinal);
            yield return new AgentResponseUpdate(
                ChatRole.Assistant,
                requests.Where(request => human.Contains(request.RequestId)).Cast<AIContent>().ToList()
            );
            yield break;
        }
    }

    /// <summary>
    /// 把本次调用的输入交给批次：没有等待中的批次时原样转发；有批次时按原请求校验并保存回答，未齐时返回 null，齐全时组合完整的原生响应批次。
    /// Hands this call's input to the batch: without a waiting batch it passes through; with one, answers are validated against the original requests and saved, returning null until complete and the full native response batch once complete.
    /// </summary>
    private static List<ChatMessage>? TakeInput(IEnumerable<ChatMessage> messages, AgentSession session)
    {
        var input = messages.ToList();
        // 可复用授权随原生响应的授权范围传递；MAF 的常驻批准包装不会进入批次。
        // Reusable grants travel as the grant scope of the native response; MAF's standing-approval wrapper never enters the batch.
        if (input.Any(message => message.Contents.Any(content => content is AlwaysApproveToolApprovalResponseContent)))
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "Standing approval wrappers are not supported; approvals carry their grant scope."
            );
        var batch = ReadBatch(session);
        if (batch == null)
        {
            if (input.Any(message => message.Contents.Any(content => content is ToolApprovalResponseContent)))
                throw new AgwException(
                    ErrorCodes.DurableExecutionConflict,
                    "No pending approval batch matches this approval response."
                );
            return input;
        }

        ChatMessage? responseMessage = null;
        foreach (var message in input)
        {
            var remaining = new List<AIContent>(message.Contents.Count);
            foreach (var content in message.Contents)
            {
                if (content is not ToolApprovalResponseContent response)
                {
                    remaining.Add(content);
                    continue;
                }

                var item =
                    batch.Items.FirstOrDefault(candidate =>
                        candidate.RequiresHuman
                        && string.Equals(candidate.Request.RequestId, response.RequestId, StringComparison.Ordinal)
                    )
                    ?? throw new AgwException(
                        ErrorCodes.DurableExecutionConflict,
                        $"Approval response '{response.RequestId}' does not match a pending request."
                    );
                if (item.Response != null)
                    throw new AgwException(
                        ErrorCodes.DurableExecutionConflict,
                        $"Approval request '{response.RequestId}' was already answered."
                    );
                EnsureSameCall(item.Request, response);
                item.Response = new ToolApprovalResponseContent(
                    item.Request.RequestId,
                    response.Approved,
                    item.Request.ToolCall
                )
                {
                    Reason = response.Reason,
                    AdditionalProperties = response.AdditionalProperties,
                };
                responseMessage = message;
            }

            // 等待期间随回答传入的普通消息保存在批次中，随完整批次一起交给内层 Agent。
            // Ordinary messages that arrive while waiting are kept in the batch and delivered with the complete batch.
            if (remaining.Count > 0)
            {
                var buffered = message.Clone();
                buffered.Contents = remaining;
                batch.BufferedMessages.Add(buffered);
            }
        }

        if (batch.Items.Any(item => item.Response == null))
        {
            SaveBatch(session, batch);
            return null;
        }

        session.StateBag.TryRemoveValue(BatchStateKey);
        var responses = responseMessage?.Clone() ?? new ChatMessage();
        responses.Role = ChatRole.User;
        responses.Contents = batch.Items.Select(item => (AIContent)item.Response!).ToList();
        return [responses, .. batch.BufferedMessages];
    }

    /// <summary>
    /// 按工具声明区分用户输入与工具审批；工具审批由 InteractionRules 根据 Turn 权限快照与有效授权决定。
    /// Separates user input from tool approvals by tool declaration; tool approvals are decided by InteractionRules from the turn permission snapshot and effective grants.
    /// </summary>
    private async Task<List<MafApprovalBatchItem>> DecideAsync(
        IReadOnlyList<ToolApprovalRequestContent> requests,
        AgentSession session,
        AgentRunOptions? options,
        IReadOnlyCollection<ChatMessage> requestMessages,
        CancellationToken cancellationToken
    )
    {
        var source = HumanInteractionToolMetadata.CurrentSource(_interactions);
        var scopeId = source.ProviderScopeId ?? source.NodeId!;
        var mode = _interactions?.PermissionState is { } permissions
            ? MafSessionApprovalState.Synchronize(session, permissions)
            : MafSessionApprovalState.GetPermissionMode(session);
        var items = new List<MafApprovalBatchItem>(requests.Count);
        foreach (var approval in requests)
        {
            var request = MafApprovalAdapter.CreateRequest(
                approval,
                scopeId,
                source.NodeName ?? Name,
                _interactions?.Requests
            );
            var granted =
                request is ToolApprovalInteraction
                && approval.ToolCall is FunctionCallContent call
                && await IsGrantedAsync(call, session, options, requestMessages, mode).ConfigureAwait(false);
            var decision = InteractionRules.AutomaticallyApprove(request, mode, granted);
            items.Add(
                new MafApprovalBatchItem
                {
                    Request = Snapshot(approval),
                    RequiresHuman = decision == null,
                    Response =
                        decision == null
                            ? null
                            : (ToolApprovalResponseContent)MafApprovalAdapter.CreateResponse(approval, decision),
                }
            );
        }
        return items;
    }

    private async ValueTask<bool> IsGrantedAsync(
        FunctionCallContent call,
        AgentSession session,
        AgentRunOptions? options,
        IReadOnlyCollection<ChatMessage> requestMessages,
        AgwPermissionMode? mode
    )
    {
        var context = new ToolAutoApprovalRuleContext(call, this, session, requestMessages, options);
        if (MafSessionApprovalState.TryApprove(context, mode))
            return true;
        foreach (var rule in _autoApprovalRules)
            if (await rule(context).ConfigureAwait(false))
                return true;
        return false;
    }

    /// <summary>
    /// 为需要用户决定的请求建立批次：批次 ID 由节点与请求身份生成，Turn、activation 与 Step 取自当前执行作用域。
    /// Creates the batch for requests that need a user: the batch ID derives from the node and request identities, and the turn, activation and Step come from the current execution scope.
    /// </summary>
    private MafApprovalBatchState CreateBatch(List<MafApprovalBatchItem> items)
    {
        var scope = ExecutionScope.Required;
        var nodeId = HumanInteractionToolMetadata.CurrentSource(_interactions).NodeId!;
        return new MafApprovalBatchState
        {
            BatchId = InteractionIdentity.ForBatch(
                nodeId,
                items.Where(item => item.RequiresHuman).Select(item => item.Request.RequestId)
            ),
            TurnId = scope.Context.TurnId,
            NodeId = nodeId,
            ActivationIndex = scope.Context.Node?.ActivationIndex ?? 0,
            StepIndex = scope.StepIndex,
            Items = items,
        };
    }

    /// <summary>
    /// 全部请求都已自动批准时，在当前外层运行中把原生批准响应交回内层 Agent。
    /// When every request was approved automatically, the native approvals go back to the inner Agent within this outer run.
    /// </summary>
    private static List<ChatMessage> ContinueAutomatically(List<MafApprovalBatchItem> items) =>
        [new ChatMessage(ChatRole.User, items.Select(item => (AIContent)item.Response!).ToList())];

    private static void SaveBatch(AgentSession session, MafApprovalBatchState batch) =>
        session.StateBag.SetValue(BatchStateKey, batch, JsonOptions);

    /// <summary>
    /// 回答必须指向原请求的同一个调用：调用 ID、工具名称与参数都一致。
    /// An answer must point at the same call as its original request: call ID, tool name and arguments all match.
    /// </summary>
    private static void EnsureSameCall(ToolApprovalRequestContent request, ToolApprovalResponseContent response)
    {
        var same =
            string.Equals(response.ToolCall.CallId, request.ToolCall.CallId, StringComparison.Ordinal)
            && (response.ToolCall, request.ToolCall) switch
            {
                (FunctionCallContent answered, FunctionCallContent original) => string.Equals(
                    answered.Name,
                    original.Name,
                    StringComparison.Ordinal
                )
                    && JsonNode.DeepEquals(
                        JsonSerializer.SerializeToNode(answered.Arguments, JsonOptions),
                        JsonSerializer.SerializeToNode(original.Arguments, JsonOptions)
                    ),
                _ => response.ToolCall.GetType() == request.ToolCall.GetType(),
            };
        if (!same)
            throw new AgwException(
                ErrorCodes.DurableExecutionConflict,
                $"Approval response '{response.RequestId}' does not match its original tool call."
            );
    }

    /// <summary>
    /// 保存请求的独立副本，调用方修改输出对象不会改变批次内容；复制同时确认它可以随 Session 序列化。
    /// Keeps an independent copy of the request so caller changes to the emitted object cannot alter the batch; copying also proves it serializes with the session.
    /// </summary>
    private static ToolApprovalRequestContent Snapshot(ToolApprovalRequestContent request) =>
        JsonSerializer.Deserialize<ToolApprovalRequestContent>(
            JsonSerializer.SerializeToElement(request, JsonOptions),
            JsonOptions
        )!;

    private static List<ChatMessage> RemoveRequests(IEnumerable<ChatMessage> messages, IReadOnlySet<string> requestIds)
    {
        var result = new List<ChatMessage>();
        foreach (var message in messages)
        {
            if (
                !message.Contents.Any(content =>
                    content is ToolApprovalRequestContent request && requestIds.Contains(request.RequestId)
                )
            )
            {
                result.Add(message);
                continue;
            }

            var remaining = message
                .Contents.Where(content =>
                    content is not ToolApprovalRequestContent request || !requestIds.Contains(request.RequestId)
                )
                .ToList();
            if (remaining.Count == 0)
                continue;
            var filtered = message.Clone();
            filtered.Contents = remaining;
            result.Add(filtered);
        }
        return result;
    }

    private static AgentResponse CreateResponse(AgentResponse last, List<ChatMessage> messages, UsageDetails? usage) =>
        new(messages)
        {
            AgentId = last.AgentId,
            ResponseId = last.ResponseId,
            CreatedAt = last.CreatedAt,
            AdditionalProperties = last.AdditionalProperties,
            Usage = usage,
        };

    private static AgentResponseUpdate CloneUpdate(AgentResponseUpdate update, IList<AIContent> contents) =>
        new(update.Role, contents)
        {
            AuthorName = update.AuthorName,
            AdditionalProperties = update.AdditionalProperties,
            AgentId = update.AgentId,
            ResponseId = update.ResponseId,
            MessageId = update.MessageId,
            CreatedAt = update.CreatedAt,
            ContinuationToken = update.ContinuationToken,
            FinishReason = update.FinishReason,
            RawRepresentation = update.RawRepresentation,
        };
}
