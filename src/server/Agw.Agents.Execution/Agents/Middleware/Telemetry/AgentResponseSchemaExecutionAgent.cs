using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.Middleware.Telemetry;

/// <summary>
/// <para>为启用结构化输出的 Agent 标记 Result 格式、记录执行指标，并转发资源释放。</para>
/// <para>Marks Result formats, records execution metrics for schema-enabled agents, and forwards resource disposal.</para>
/// </summary>
/// <remarks>
/// <para>致命 ErrorContent 和非调用方取消异常计为失败。非流式在 finally 中计数；流式在释放内层枚举器成功后计数。</para>
/// <para>Fatal ErrorContent and exceptions other than caller-requested cancellation count as failures. Non-streaming execution records in finally; streaming execution records after successful disposal of the inner enumerator.</para>
/// </remarks>
internal sealed class AgentResponseSchemaExecutionAgent : DelegatingAIAgent, IAsyncDisposable
{
    private readonly string _agentType;
    private readonly string _externalAgentKind;
    private readonly string _providerType;

    /// <summary>
    /// <para>创建 AgentResponseSchemaExecutionAgent 实例并保存本包装层使用的依赖和配置。</para>
    /// <para>Initializes AgentResponseSchemaExecutionAgent with the dependencies and configuration used by this wrapper.</para>
    /// </summary>
    /// <param name="innerAgent">
    /// <para>由当前中间件继续调用的内层 Agent。</para>
    /// <para>Inner agent invoked by this middleware.</para>
    /// </param>
    /// <param name="agentType">
    /// <para>用于指标归属的 Agent 类型标签。</para>
    /// <para>Agent-type label used for metric attribution.</para>
    /// </param>
    /// <param name="externalAgentKind">
    /// <para>外部引擎类型标签；非外部 Agent 使用约定占位值。</para>
    /// <para>External-engine label, using the established sentinel for non-external agents.</para>
    /// </param>
    /// <param name="providerType">
    /// <para>模型 Provider 类型标签。</para>
    /// <para>Model-provider type label.</para>
    /// </param>
    public AgentResponseSchemaExecutionAgent(
        AIAgent innerAgent,
        string agentType,
        string externalAgentKind,
        string providerType
    )
        : base(innerAgent)
    {
        _agentType = agentType;
        _externalAgentKind = externalAgentKind;
        _providerType = providerType;
    }

    /// <summary>
    /// <para>执行内层 Agent，检测致命错误或异常，并在执行边界记录结构化输出指标。</para>
    /// <para>Runs the inner agent, detects fatal content or exceptions, and records structured-output metrics at the execution boundary.</para>
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
    /// <param name="cancellationToken">
    /// <para>用于取消当前异步操作的令牌。</para>
    /// <para>Token used to cancel the current asynchronous operation.</para>
    /// </param>
    /// <returns>
    /// <para>完成当前包装层处理后的完整响应。</para>
    /// <para>Complete response after processing by this wrapper.</para>
    /// </returns>
    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var failed = false;
        try
        {
            var response = await InnerAgent
                .RunAsync(messages, session, options, cancellationToken)
                .ConfigureAwait(false);
            failed = HasFatalError(response.Messages.SelectMany(message => message.Contents));
            foreach (var message in response.Messages)
            {
                ResponseSchemaResultMetadata.Apply(message);
            }
            return response;
        }
        catch (Exception exception) when (!IsCancellation(exception, cancellationToken))
        {
            failed = true;
            throw;
        }
        finally
        {
            // 在执行边界统一计数；消费者取消不被额外标为结构化输出失败。
            // Record at the execution boundary without additionally classifying consumer cancellation as a schema failure.
            AgentResponseSchemaTelemetry.Record(_agentType, _externalAgentKind, _providerType, failed);
        }
    }

    /// <summary>
    /// <para>执行内层 Agent，检测致命错误或异常，并在执行边界记录结构化输出指标。</para>
    /// <para>Runs the inner agent, detects fatal content or exceptions, and records structured-output metrics at the execution boundary.</para>
    /// </summary>
    /// <remarks>
    /// <para>逐次检查 MoveNextAsync 异常和致命内容；先释放内层枚举器再写指标，释放失败会传播并跳过该次指标写入。</para>
    /// <para>Checks each MoveNextAsync failure and fatal content item; disposes the inner enumerator before recording metrics, so a disposal failure propagates and skips that metric write.</para>
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
    /// <param name="cancellationToken">
    /// <para>用于取消当前异步操作的令牌。</para>
    /// <para>Token used to cancel the current asynchronous operation.</para>
    /// </param>
    /// <returns>
    /// <para>经当前包装层处理后按顺序产生的响应更新流。</para>
    /// <para>Ordered response-update stream after processing by this wrapper.</para>
    /// </returns>
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var failed = false;
        var enumerator = InnerAgent
            .RunStreamingAsync(messages, session, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                AgentResponseUpdate update;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    update = enumerator.Current;
                }
                catch (Exception exception) when (!IsCancellation(exception, cancellationToken))
                {
                    failed = true;
                    throw;
                }

                if (!failed && HasFatalError(update.Contents))
                {
                    failed = true;
                }

                ResponseSchemaResultMetadata.Apply(update);
                yield return update;
            }
        }
        finally
        {
            // 先释放内层流；释放异常继续传播，不在其后强行写入完成指标。
            // Dispose the inner stream first; disposal failures propagate and prevent the subsequent metric write.
            await enumerator.DisposeAsync().ConfigureAwait(false);
            // 在执行边界统一计数；消费者取消不被额外标为结构化输出失败。
            // Record at the execution boundary without additionally classifying consumer cancellation as a schema failure.
            AgentResponseSchemaTelemetry.Record(_agentType, _externalAgentKind, _providerType, failed);
        }
    }

    /// <summary>
    /// <para>将资源释放转发给内层 Agent，优先异步释放，否则使用同步释放。</para>
    /// <para>Forwards resource disposal to the inner agent, preferring asynchronous disposal with synchronous disposal as a fallback.</para>
    /// </summary>
    /// <returns>
    /// <para>表示上述异步处理完成的任务。</para>
    /// <para>Task representing completion of the asynchronous operation described above.</para>
    /// </returns>
    public async ValueTask DisposeAsync()
    {
        switch (InnerAgent)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    /// <summary>
    /// <para>仅把调用令牌已取消时的取消异常识别为调用方取消。</para>
    /// <para>Recognizes an exception as caller cancellation only when it is a cancellation exception and the supplied token is cancelled.</para>
    /// </summary>
    /// <param name="exception">
    /// <para>待识别的执行异常。</para>
    /// <para>Execution exception to classify.</para>
    /// </param>
    /// <param name="cancellationToken">
    /// <para>用于取消当前异步操作的令牌。</para>
    /// <para>Token used to cancel the current asynchronous operation.</para>
    /// </param>
    /// <returns>
    /// <para>异常属于调用方请求的取消时为 true。</para>
    /// <para>True when the exception represents caller-requested cancellation.</para>
    /// </returns>
    private static bool IsCancellation(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested;

    /// <summary>
    /// <para>检查 ErrorContent 是否通过布尔 isFatalError 属性明确标记为致命错误。</para>
    /// <para>Checks whether ErrorContent explicitly carries the boolean isFatalError flag.</para>
    /// </summary>
    /// <param name="contents">
    /// <para>按原始顺序提供的消息内容项。</para>
    /// <para>Message content items in their original order.</para>
    /// </param>
    /// <returns>
    /// <para>至少一条错误内容明确标记为致命时为 true。</para>
    /// <para>True when at least one error content item is explicitly marked fatal.</para>
    /// </returns>
    private static bool HasFatalError(IEnumerable<AIContent> contents) =>
        contents
            .OfType<ErrorContent>()
            .Any(content =>
                content.AdditionalProperties?.TryGetValue("isFatalError", out var value) == true && value is true
            );
}
