using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agents.Middleware.Telemetry;

/// <summary>
/// <para>统一记录 Agent 的输入输出日志及项目会话用量。</para>
/// <para>Records agent input/output logs and project-conversation usage in one execution wrapper.</para>
/// </summary>
/// <remarks>
/// <para>可作为单例使用；消息和用量累计仅属于当前调用。流式 finally 记录已观测用量，统计失败不覆盖执行结果，只有正常完成才记录完成日志。</para>
/// <para>Safe for singleton registration because message and usage accumulators are per call. Streaming finally records observed usage without masking execution failures; completion logs are emitted only after normal completion.</para>
/// </remarks>
public sealed class AgentTelemetryMiddleware
{
    private const string UnknownAgentName = "$unknown";

    private readonly IProviderSessionState _providerSessionState;
    private readonly IAgentUsageRecorder _usageRecorder;
    private readonly ILogger<AgentTelemetryMiddleware> _logger;

    /// <summary>
    /// <para>创建 AgentTelemetryMiddleware 实例并保存本包装层使用的依赖和配置。</para>
    /// <para>Initializes AgentTelemetryMiddleware with the dependencies and configuration used by this wrapper.</para>
    /// </summary>
    /// <param name="providerSessionState">
    /// <para>从 SDK 会话解析项目和上下文归属的服务。</para>
    /// <para>Service resolving project and context attribution from SDK sessions.</para>
    /// </param>
    /// <param name="usageRecorder">
    /// <para>将统计结果写入项目会话用量的记录器。</para>
    /// <para>Recorder that writes usage totals to project conversations.</para>
    /// </param>
    /// <param name="logger">
    /// <para>记录执行、兼容处理或清理错误的日志器。</para>
    /// <para>Logger for execution, compatibility handling, or cleanup errors.</para>
    /// </param>
    public AgentTelemetryMiddleware(
        IProviderSessionState providerSessionState,
        IAgentUsageRecorder usageRecorder,
        ILogger<AgentTelemetryMiddleware> logger
    )
    {
        _providerSessionState = providerSessionState;
        _usageRecorder = usageRecorder;
        _logger = logger;
    }

    /// <summary>
    /// <para>记录原始输入，执行 Agent，结算用量后记录成功完成日志。</para>
    /// <para>Logs original input, executes the agent, and records successful completion after accounting for usage.</para>
    /// </summary>
    /// <remarks>
    /// <para>在 finally 中结算已收到的用量，涵盖异常、取消和提前释放；不为未收到的片段计费。</para>
    /// <para>Accounts for received usage in finally on failure, cancellation, or early disposal; unseen fragments are not counted.</para>
    /// </remarks>
    /// <param name="messages">
    /// <para>按调用顺序提供的聊天消息。</para>
    /// <para>Chat messages in invocation order.</para>
    /// </param>
    /// <param name="session">
    /// <para>当前 SDK 会话；可以为空，相关状态处理会按方法规则跳过或委托内层。</para>
    /// <para>Current SDK session; may be null, with state handling skipped or delegated according to the method contract.</para>
    /// </param>
    /// <param name="options">
    /// <para>本次调用选项；为空时由后续执行层处理默认值。</para>
    /// <para>Options for this call; downstream execution handles defaults when null.</para>
    /// </param>
    /// <param name="innerAgent">
    /// <para>由当前中间件继续调用的内层 Agent。</para>
    /// <para>Inner agent invoked by this middleware.</para>
    /// </param>
    /// <param name="cancellationToken">
    /// <para>用于取消当前异步操作的令牌。</para>
    /// <para>Token used to cancel the current asynchronous operation.</para>
    /// </param>
    /// <returns>
    /// <para>经当前包装层处理后按顺序产生的响应更新流。</para>
    /// <para>Ordered response-update stream after processing by this wrapper.</para>
    /// </returns>
    public async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var agentName = innerAgent.Name;
        var inputMessages = messages.ToList();
        _logger.LogInformation("Executing agent {AgentName}", agentName);
        _logger.LogDebug("Agent {AgentName} input: {@Input}", agentName, inputMessages);
        List<AgentResponseUpdate> updates = [];
        // 累计器属于当前枚举，不存放在单例字段中，避免并行执行相互混入用量。
        // Keep the accumulator local to this enumeration so concurrent singleton calls cannot mix usage.
        var combinedUsage = new UsageDetails();
        var hasUsage = false;

        try
        {
            await foreach (
                var update in innerAgent.RunStreamingAsync(inputMessages, session, options, cancellationToken)
            )
            {
                foreach (var usageContent in update.Contents.OfType<UsageContent>())
                {
                    combinedUsage.Add(usageContent.Details);
                    hasUsage = true;
                }

                updates.Add(update);
                yield return update;
            }
        }
        finally
        {
            // finally 仅结算已观测的用量，覆盖正常结束、异常、取消和提前释放。
            // Settle only observed usage in finally on completion, failure, cancellation, or early disposal.
            if (hasUsage)
            {
                await RecordAsync(session, ResolveAgentName(innerAgent), combinedUsage);
            }
        }

        _logger.LogInformation("Executed agent {AgentName}", agentName);
        _logger.LogDebug("Agent {AgentName} output: {@Output}", agentName, updates.ToAgentResponse());
    }

    /// <summary>
    /// <para>记录原始输入，执行 Agent，结算用量后记录成功完成日志。</para>
    /// <para>Logs original input, executes the agent, and records successful completion after accounting for usage.</para>
    /// </summary>
    /// <param name="messages">
    /// <para>按调用顺序提供的聊天消息。</para>
    /// <para>Chat messages in invocation order.</para>
    /// </param>
    /// <param name="session">
    /// <para>当前 SDK 会话；可以为空，相关状态处理会按方法规则跳过或委托内层。</para>
    /// <para>Current SDK session; may be null, with state handling skipped or delegated according to the method contract.</para>
    /// </param>
    /// <param name="options">
    /// <para>本次调用选项；为空时由后续执行层处理默认值。</para>
    /// <para>Options for this call; downstream execution handles defaults when null.</para>
    /// </param>
    /// <param name="innerAgent">
    /// <para>由当前中间件继续调用的内层 Agent。</para>
    /// <para>Inner agent invoked by this middleware.</para>
    /// </param>
    /// <param name="cancellationToken">
    /// <para>用于取消当前异步操作的令牌。</para>
    /// <para>Token used to cancel the current asynchronous operation.</para>
    /// </param>
    /// <returns>
    /// <para>完成当前包装层处理后的完整响应。</para>
    /// <para>Complete response after processing by this wrapper.</para>
    /// </returns>
    public async Task<AgentResponse> RunAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken
    )
    {
        var agentName = innerAgent.Name;
        var inputMessages = messages.ToList();
        _logger.LogInformation("Executing agent {AgentName}", agentName);
        _logger.LogDebug("Agent {AgentName} input: {@Input}", agentName, inputMessages);
        var response = await innerAgent
            .RunAsync(inputMessages, session, options, cancellationToken)
            .ConfigureAwait(false);
        if (response.Usage != null)
        {
            await RecordAsync(session, ResolveAgentName(innerAgent), response.Usage);
        }

        _logger.LogInformation("Executed agent {AgentName}", agentName);
        _logger.LogDebug("Agent {AgentName} output: {@Output}", agentName, response);
        return response;
    }

    /// <summary>
    /// <para>从会话解析项目和上下文，以不可取消令牌写入用量；无归属时跳过，记录器失败只写日志。</para>
    /// <para>Resolves project/context from the session and records usage with a non-cancellable token; skips missing attribution and logs recorder failures without rethrowing.</para>
    /// </summary>
    /// <param name="session">
    /// <para>当前 SDK 会话；可以为空，相关状态处理会按方法规则跳过或委托内层。</para>
    /// <para>Current SDK session; may be null, with state handling skipped or delegated according to the method contract.</para>
    /// </param>
    /// <param name="agentName">
    /// <para>用于用量归属的 Agent 名称或兜底键。</para>
    /// <para>Agent name or fallback key used for usage attribution.</para>
    /// </param>
    /// <param name="usage">
    /// <para>本次响应或已观测流片段累计的用量。</para>
    /// <para>Usage from this response or accumulated observed stream fragments.</para>
    /// </param>
    /// <returns>
    /// <para>表示上述异步处理完成的任务。</para>
    /// <para>Task representing completion of the asynchronous operation described above.</para>
    /// </returns>
    private async Task RecordAsync(AgentSession? session, string agentName, UsageDetails usage)
    {
        if (
            session == null
            || !_providerSessionState.TryGetProjectContext(session, out var projectId, out var contextId)
        )
        {
            return;
        }

        try
        {
            await _usageRecorder.AddAsync(
                projectId,
                contextId,
                agentName,
                new ProjectContextUsage
                {
                    InputTokenCount = usage.InputTokenCount ?? 0,
                    OutputTokenCount = usage.OutputTokenCount ?? 0,
                    TotalTokenCount = usage.TotalTokenCount ?? 0,
                    CachedInputTokenCount = usage.CachedInputTokenCount ?? 0,
                    ReasoningTokenCount = usage.ReasoningTokenCount ?? 0,
                },
                // 执行取消后仍需记录已消耗用量，因此统计写入使用独立的不可取消令牌。
                // Consumed usage must still be recorded after execution cancellation, so use a non-cancellable token.
                CancellationToken.None
            );
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Failed to record agent usage for project {ProjectId} and context {ContextId}.",
                projectId,
                contextId
            );
        }
    }

    /// <summary>
    /// <para>返回 Agent 名称，空白名称使用稳定的 $unknown 归属键。</para>
    /// <para>Returns the agent name or the stable $unknown attribution key when the name is blank.</para>
    /// </summary>
    /// <param name="agent">
    /// <para>待读取名称或执行元信息的 Agent。</para>
    /// <para>Agent whose name or execution metadata is read.</para>
    /// </param>
    /// <returns>
    /// <para>非空白名称或 $unknown 兜底值。</para>
    /// <para>Nonblank name or the $unknown fallback.</para>
    /// </returns>
    private static string ResolveAgentName(AIAgent agent) =>
        string.IsNullOrWhiteSpace(agent.Name) ? UnknownAgentName : agent.Name;
}
