using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

#pragma warning disable OPENAI001 // Responses API types are marked experimental by the SDK.
#pragma warning disable SCME0001 // DeepSeek's reasoning.content is exposed through JsonPatch.

namespace Agw.Agents.Execution.Agents.Middleware.ModelInput;

/// <summary>
/// <para>在 DeepSeek Responses 协议与可移植推理内容之间进行转换。</para>
/// <para>Adapts between DeepSeek Responses protocol fields and portable reasoning content.</para>
/// </summary>
/// <remarks>
/// <para>仅 DeepSeek 端点启用此适配；不会把其他协议的不透明签名转换为 encrypted_content。</para>
/// <para>Enables this adapter only for DeepSeek endpoints and never maps another protocol's opaque signature to encrypted_content.</para>
/// </remarks>
internal sealed class OpenAiResponsesReasoningChatClient : DelegatingChatClient
{
    /// <summary>
    /// <para>创建 OpenAiResponsesReasoningChatClient 实例并保存本包装层使用的依赖和配置。</para>
    /// <para>Initializes OpenAiResponsesReasoningChatClient with the dependencies and configuration used by this wrapper.</para>
    /// </summary>
    /// <param name="innerClient">
    /// <para>接收处理后请求的内层聊天客户端。</para>
    /// <para>Inner chat client receiving the processed request.</para>
    /// </param>
    private OpenAiResponsesReasoningChatClient(IChatClient innerClient)
        : base(innerClient) { }

    /// <summary>
    /// <para>仅为 DeepSeek 端点安装 Responses 推理适配，其他端点返回原客户端。</para>
    /// <para>Installs the Responses reasoning adapter only for DeepSeek, returning the original client for other endpoints.</para>
    /// </summary>
    /// <param name="client">
    /// <para>待适配的 SDK 或聊天客户端。</para>
    /// <para>SDK or chat client to adapt.</para>
    /// </param>
    /// <param name="endpoint">
    /// <para>模型服务端点，用于选择特定主机的兼容规则。</para>
    /// <para>Model-service endpoint used to select host-specific compatibility rules.</para>
    /// </param>
    /// <returns>
    /// <para>DeepSeek 适配器，或无需转换时的原客户端。</para>
    /// <para>DeepSeek adapter, or the original client when conversion is unnecessary.</para>
    /// </returns>
    public static IChatClient Create(IChatClient client, Uri endpoint) =>
        endpoint.Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase)
            ? new OpenAiResponsesReasoningChatClient(client)
            : client;

    /// <summary>
    /// <para>转换请求中的推理内容，并从 DeepSeek 原生响应的 content 字段恢复非流式推理正文。</para>
    /// <para>Converts request reasoning and restores non-streaming reasoning text from DeepSeek native response content fields.</para>
    /// </summary>
    /// <param name="messages">
    /// <para>按调用顺序提供的聊天消息。</para>
    /// <para>Chat messages in invocation order.</para>
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
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var response = await base.GetResponseAsync(ProjectReasoning(messages), options, cancellationToken)
            .ConfigureAwait(false);
        foreach (
            var reasoning in response.Messages.SelectMany(message => message.Contents).OfType<TextReasoningContent>()
        )
        {
            // MEAI 非流式默认只读取 summary；从 DeepSeek 的 content 中恢复实际推理正文。
            // MEAI reads summary only for non-streaming responses. DeepSeek returns content.
            if (reasoning.RawRepresentation is ReasoningResponseItem item && item.Patch.Contains("$.content"u8))
            {
                using var content = JsonDocument.Parse(item.Patch.GetJson("$.content"u8));
                reasoning.Text = string.Concat(
                    content
                        .RootElement.EnumerateArray()
                        .Where(part => part.GetProperty("type").GetString() == "reasoning_text")
                        .Select(part => part.GetProperty("text").GetString())
                );
            }
        }
        return response;
    }

    /// <summary>
    /// <para>将历史推理映射为 DeepSeek Responses 的明文 content，并保留结果消息。</para>
    /// <para>Maps historical reasoning to DeepSeek Responses plain-text content and preserves result messages.</para>
    /// </summary>
    /// <param name="messages">
    /// <para>按调用顺序提供的聊天消息。</para>
    /// <para>Chat messages in invocation order.</para>
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
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    ) => base.GetStreamingResponseAsync(ProjectReasoning(messages), options, cancellationToken);

    /// <summary>
    /// <para>复制包含推理的 Assistant 消息，将推理文本投影为 Responses content 并保留推理条目 ID。</para>
    /// <para>Copies assistant messages containing reasoning, projects reasoning text into Responses content, and preserves reasoning-item IDs.</para>
    /// </summary>
    /// <param name="messages">
    /// <para>按调用顺序提供的聊天消息。</para>
    /// <para>Chat messages in invocation order.</para>
    /// </param>
    /// <returns>
    /// <para>包含 Responses 原生推理条目的消息序列。</para>
    /// <para>Message sequence containing native Responses reasoning items.</para>
    /// </returns>
    private static IEnumerable<ChatMessage> ProjectReasoning(IEnumerable<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            if (message.Role != ChatRole.Assistant || !message.Contents.OfType<TextReasoningContent>().Any())
            {
                yield return message;
                continue;
            }

            // 仅重建含推理的 Assistant 消息，普通消息直接保留。
            // Rebuild only assistant messages containing reasoning; ordinary messages pass through unchanged.
            var copy = message.Clone();
            copy.Contents = message
                .Contents.Select(content =>
                {
                    if (content is not TextReasoningContent reasoning)
                        return content;

                    var item = new ReasoningResponseItem
                    {
                        Id =
                            (reasoning.RawRepresentation as ReasoningResponseItem)?.Id
                            ?? (
                                reasoning.AdditionalProperties?.TryGetValue("reasoningItemId", out var id) == true
                                    ? id?.ToString()
                                    : null
                            ),
                    };
                    item.Patch.Set(
                        "$.content"u8,
                        BinaryData.FromObjectAsJson(new[] { new { type = "reasoning_text", text = reasoning.Text } })
                    );
                    // DeepSeek 不支持 summary 或 encrypted_content，不能把其他协议的不透明签名作为加密推理转发。
                    // DeepSeek does not support summary or encrypted_content. Never forward a
                    // different protocol's opaque signature as encrypted reasoning to this endpoint.
                    return new TextReasoningContent(reasoning.Text)
                    {
                        RawRepresentation = item,
                        AdditionalProperties = reasoning.AdditionalProperties,
                        Annotations = reasoning.Annotations,
                    };
                })
                .ToList();
            yield return copy;
        }
    }
}
