using System.Runtime.CompilerServices;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.Middleware.ModelInput;

/// <summary>
/// <para>在 Anthropic 历史重放中保留 thinking 与 redacted_thinking 的协议含义。</para>
/// <para>Preserves thinking and redacted_thinking protocol semantics when replaying Anthropic history.</para>
/// </summary>
/// <remarks>
/// <para>流式片段合并前记录 thinking 类型；DeepSeek 缺少 thinking 时补空块，其他服务不按空文本推断块类型。</para>
/// <para>Records thinking types before streaming fragments are coalesced. Adds an empty thinking block when DeepSeek requires one, without inferring block types from empty text on other services.</para>
/// </remarks>
internal sealed class AnthropicReasoningChatClient : DelegatingChatClient
{
    private const string ThinkingTypeProperty = "agw.anthropic.thinkingType";
    private readonly bool _requiresThinking;

    /// <summary>
    /// <para>创建 AnthropicReasoningChatClient 实例并保存本包装层使用的依赖和配置。</para>
    /// <para>Initializes AnthropicReasoningChatClient with the dependencies and configuration used by this wrapper.</para>
    /// </summary>
    /// <param name="innerClient">
    /// <para>接收处理后请求的内层聊天客户端。</para>
    /// <para>Inner chat client receiving the processed request.</para>
    /// </param>
    /// <param name="requiresThinking">
    /// <para>是否必须为缺少 thinking 的 Assistant 历史补空块。</para>
    /// <para>Whether assistant history without thinking requires an empty block.</para>
    /// </param>
    private AnthropicReasoningChatClient(IChatClient innerClient, bool requiresThinking)
        : base(innerClient)
    {
        _requiresThinking = requiresThinking;
    }

    /// <summary>
    /// <para>创建 Anthropic 推理适配器，并仅对 DeepSeek 启用必需 thinking 补齐。</para>
    /// <para>Creates an Anthropic reasoning adapter, enabling required-thinking completion only for DeepSeek.</para>
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
    /// <para>配置好端点兼容规则的聊天客户端。</para>
    /// <para>Chat client configured with endpoint-specific compatibility rules.</para>
    /// </returns>
    public static IChatClient Create(IChatClient client, Uri endpoint) =>
        new AnthropicReasoningChatClient(
            client,
            endpoint.Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase)
        );

    /// <summary>
    /// <para>恢复请求的 thinking 表示并标记响应的 thinking 类型。</para>
    /// <para>Restores thinking representations in requests and stamps thinking types on responses.</para>
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
        var response = await base.GetResponseAsync(PreserveThinking(messages), options, cancellationToken)
            .ConfigureAwait(false);
        foreach (var message in response.Messages)
            PreserveThinkingType(message.Contents);
        return response;
    }

    /// <summary>
    /// <para>恢复请求的 thinking 表示并标记响应的 thinking 类型。</para>
    /// <para>Restores thinking representations in requests and stamps thinking types on responses.</para>
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
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        await foreach (
            var update in base.GetStreamingResponseAsync(PreserveThinking(messages), options, cancellationToken)
                .ConfigureAwait(false)
        )
        {
            // 在 MEAI 合并片段并丢弃原生表示之前，先保存每段 thinking 的类型。
            // Stamp every fragment before MEAI coalesces it and discards RawRepresentation.
            PreserveThinkingType(update.Contents);
            yield return update;
        }
    }

    /// <summary>
    /// <para>复制 Assistant 消息，恢复可表示的 thinking 块，并在 DeepSeek 缺少推理时补空块。</para>
    /// <para>Copies assistant messages, restores representable thinking blocks, and adds an empty block when DeepSeek requires missing reasoning.</para>
    /// </summary>
    /// <param name="messages">
    /// <para>按调用顺序提供的聊天消息。</para>
    /// <para>Chat messages in invocation order.</para>
    /// </param>
    /// <returns>
    /// <para>按原顺序返回的消息序列，Assistant 消息按需转换。</para>
    /// <para>Messages in original order with assistant messages adapted as needed.</para>
    /// </returns>
    private IEnumerable<ChatMessage> PreserveThinking(IEnumerable<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            if (message.Role != ChatRole.Assistant)
            {
                yield return message;
                continue;
            }

            var contents = new List<AIContent>(message.Contents.Count + 1);
            var hasThinking = false;
            foreach (var content in message.Contents)
            {
                hasThinking |=
                    content is TextReasoningContent
                    || content.RawRepresentation
                        is ContentBlockParam { Value: ThinkingBlockParam or RedactedThinkingBlockParam };
                contents.Add(PreserveThinkingBlock(content));
            }
            // DeepSeek 的旧纯文本回答也必须有 thinking；只补空块，不借用其他回合推理或覆盖已有块。
            // DeepSeek requires thinking on older plain answers too. Never substitute an
            // earlier turn's reasoning or overwrite a block supplied by the model/caller.
            if (_requiresThinking && !hasThinking)
                contents.Insert(0, EmptyThinking());

            var copy = message.Clone();
            copy.Contents = contents;
            yield return copy;
        }
    }

    /// <summary>
    /// <para>根据已保存块类型恢复空 thinking 的原生表示，保留已有原生块和不适用的内容。</para>
    /// <para>Restores native representations for empty thinking using stored block types, preserving existing native blocks and inapplicable content.</para>
    /// </summary>
    /// <param name="content">
    /// <para>单个待检查或转换的 AI 内容项。</para>
    /// <para>Individual AI content item to inspect or transform.</para>
    /// </param>
    /// <returns>
    /// <para>恢复原生表示的推理内容，或无需处理的原内容。</para>
    /// <para>Reasoning content with restored native representation, or unchanged original content.</para>
    /// </returns>
    private AIContent PreserveThinkingBlock(AIContent content)
    {
        if (content is not TextReasoningContent reasoning || content.RawRepresentation is ContentBlockParam)
            return content;

        // 读取真实块类型后再决定是否恢复原生表示，不把遮蔽推理误当作空文本。
        // Resolve the actual block type before restoring native representation; redacted reasoning is not empty text.
        var type = GetThinkingType(reasoning);
        // 空文本不能区分块类型，签名和遮蔽数据共用 ProtectedData；优先使用已记录类型，仅旧 DeepSeek 历史允许回退。
        // Empty text is not a type discriminator: thinking.signature and redacted_thinking.data
        // both occupy ProtectedData. Prefer the recorded type; only legacy DeepSeek history
        // falls back to thinking, since that endpoint does not produce redacted_thinking.
        if (type == "redacted_thinking" || reasoning.Text.Length > 0 || (!_requiresThinking && type != "thinking"))
            return content;

        return new TextReasoningContent(reasoning.Text)
        {
            ProtectedData = reasoning.ProtectedData,
            AdditionalProperties = reasoning.AdditionalProperties,
            Annotations = reasoning.Annotations,
            RawRepresentation = new ContentBlockParam(
                new ThinkingBlockParam
                {
                    Thinking = reasoning.Text,
                    Signature = reasoning.ProtectedData ?? string.Empty,
                }
            ),
        };
    }

    /// <summary>
    /// <para>从原生块读取推理类型并写入便携属性，避免流片段合并后丢失类型。</para>
    /// <para>Copies reasoning types from native blocks to portable properties before stream coalescing can discard them.</para>
    /// </summary>
    /// <param name="contents">
    /// <para>按原始顺序提供的消息内容项。</para>
    /// <para>Message content items in their original order.</para>
    /// </param>
    private static void PreserveThinkingType(IEnumerable<AIContent> contents)
    {
        foreach (var reasoning in contents.OfType<TextReasoningContent>())
        {
            var type = GetThinkingType(reasoning);
            if (type != null)
                (reasoning.AdditionalProperties ??= [])[ThinkingTypeProperty] = type;
        }
    }

    /// <summary>
    /// <para>优先从原生块判定推理类型，无法判定时读取已持久化的类型属性。</para>
    /// <para>Resolves the reasoning type from native blocks first, falling back to the persisted type property.</para>
    /// </summary>
    /// <param name="reasoning">
    /// <para>携带推理正文、原生表示或已保存类型信息的内容。</para>
    /// <para>Reasoning content carrying text, native representation, or persisted type metadata.</para>
    /// </param>
    /// <returns>
    /// <para>thinking 或 redacted_thinking 类型字符串；无法确定时为空。</para>
    /// <para>The thinking or redacted_thinking type string, or null if unresolved.</para>
    /// </returns>
    private static string? GetThinkingType(TextReasoningContent reasoning) =>
        reasoning.RawRepresentation switch
        {
            ThinkingBlock or ThinkingDelta or SignatureDelta => "thinking",
            RedactedThinkingBlock => "redacted_thinking",
            ContentBlockParam { Value: ThinkingBlockParam } => "thinking",
            ContentBlockParam { Value: RedactedThinkingBlockParam } => "redacted_thinking",
            _ => reasoning.AdditionalProperties?.TryGetValue(ThinkingTypeProperty, out var type) == true
                ? type?.ToString()
                : null,
        };

    /// <summary>
    /// <para>创建满足 DeepSeek 协议要求的空 thinking 块及空签名。</para>
    /// <para>Creates an empty thinking block and signature required by the DeepSeek protocol.</para>
    /// </summary>
    /// <returns>
    /// <para>包含空原生 thinking 块的推理内容。</para>
    /// <para>Reasoning content containing an empty native thinking block.</para>
    /// </returns>
    private static TextReasoningContent EmptyThinking() =>
        new(string.Empty)
        {
            RawRepresentation = new ContentBlockParam(
                new ThinkingBlockParam { Thinking = string.Empty, Signature = string.Empty }
            ),
        };
}
