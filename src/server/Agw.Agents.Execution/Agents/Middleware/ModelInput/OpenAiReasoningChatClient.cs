using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agw.Agents.Execution.Agents.Middleware.ModelInput;

/// <summary>
/// <para>为兼容 Chat Completions 的服务恢复可移植历史中的 reasoning_content 字段。</para>
/// <para>Restores reasoning_content from portable history for compatible Chat Completions services.</para>
/// </summary>
/// <remarks>
/// <para>原生 OpenAI 和 Azure 端点不使用此包装器。每次调用建立独立请求策略，避免并行请求共享推理补丁。</para>
/// <para>Native OpenAI and Azure endpoints bypass this wrapper. Each call gets its own request policy so concurrent requests cannot share reasoning patches.</para>
/// </remarks>
internal sealed class OpenAiReasoningChatClient : DelegatingChatClient
{
    private readonly ChatClient _client;
    private readonly bool _requiresReasoningContent;

    /// <summary>
    /// <para>创建 OpenAiReasoningChatClient 实例并保存本包装层使用的依赖和配置。</para>
    /// <para>Initializes OpenAiReasoningChatClient with the dependencies and configuration used by this wrapper.</para>
    /// </summary>
    /// <param name="client">
    /// <para>待适配的 SDK 或聊天客户端。</para>
    /// <para>SDK or chat client to adapt.</para>
    /// </param>
    /// <param name="requiresReasoningContent">
    /// <para>是否为缺少推理字段的 Assistant 消息补空字符串。</para>
    /// <para>Whether assistant messages missing reasoning fields require an empty string.</para>
    /// </param>
    private OpenAiReasoningChatClient(ChatClient client, bool requiresReasoningContent)
        : base(client.AsIChatClient())
    {
        _client = client;
        _requiresReasoningContent = requiresReasoningContent;
    }

    /// <summary>
    /// <para>为原生 OpenAI 或 Azure 返回标准适配器，为其他端点启用 reasoning_content 兼容处理。</para>
    /// <para>Returns a standard adapter for native OpenAI or Azure and enables reasoning_content compatibility for other endpoints.</para>
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
    /// <para>标准 SDK 适配器或启用厂商兼容规则的包装器。</para>
    /// <para>Standard SDK adapter or wrapper with vendor-compatibility rules enabled.</para>
    /// </returns>
    public static IChatClient Create(ChatClient client, Uri endpoint) =>
        // 原生 OpenAI 使用自身推理协议，不向这些端点发送其他厂商的推理扩展字段。
        // Native OpenAI uses its own reasoning protocol. Do not send a vendor extension
        // there when portable history originated from another model.
        endpoint.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase)
        || endpoint.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase)
            ? client.AsIChatClient()
            : new OpenAiReasoningChatClient(
                client,
                endpoint.Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase)
            );

    /// <summary>
    /// <para>创建仅属于本次请求的推理补丁策略，在调用结束或流释放时释放适配器。</para>
    /// <para>Creates a request-local reasoning patch policy and disposes the adapter when the call or stream ends.</para>
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
        var inputs = messages as IList<ChatMessage> ?? messages.ToList();
        using var client = CreateRequestClient(inputs, options);
        return await client.GetResponseAsync(inputs, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <para>创建仅属于本次请求的推理补丁策略，在调用结束或流释放时释放适配器。</para>
    /// <para>Creates a request-local reasoning patch policy and disposes the adapter when the call or stream ends.</para>
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
        var inputs = messages as IList<ChatMessage> ?? messages.ToList();
        using var client = CreateRequestClient(inputs, options);
        await foreach (
            var update in client.GetStreamingResponseAsync(inputs, options, cancellationToken).ConfigureAwait(false)
        )
            yield return update;
    }

    /// <summary>
    /// <para>按 SDK 线协议消息索引收集推理，并为当前请求安装独立的补丁策略。</para>
    /// <para>Collects reasoning by SDK wire-message index and installs a patch policy isolated to the current request.</para>
    /// </summary>
    /// <param name="messages">
    /// <para>按调用顺序提供的聊天消息。</para>
    /// <para>Chat messages in invocation order.</para>
    /// </param>
    /// <param name="options">
    /// <para>本次调用选项；为空时由后续执行层处理默认值。</para>
    /// <para>Options for this call; downstream execution handles defaults when null.</para>
    /// </param>
    /// <returns>
    /// <para>当前请求独享的适配器；调用方负责释放。</para>
    /// <para>Adapter owned by this request; the caller must dispose it.</para>
    /// </returns>
    private IChatClient CreateRequestClient(IList<ChatMessage> messages, ChatOptions? options)
    {
        var reasoningByIndex = new Dictionary<int, string>();
        // Instructions 会占据一条线协议消息，因此先调整后续推理补丁的索引起点。
        // Instructions occupy one wire message, so offset the indexes used by later reasoning patches.
        var wireIndex = string.IsNullOrWhiteSpace(options?.Instructions) ? 0 : 1;
        string? precedingReasoning = null;
        string? precedingAuthor = null;
        foreach (var message in messages)
        {
            if (message.Role != ChatRole.Assistant || message.AuthorName != precedingAuthor)
                precedingReasoning = null;

            var reasoningOnly = false;
            if (message.Role == ChatRole.Assistant)
            {
                var reasoning = message.Contents.OfType<TextReasoningContent>().ToList();
                if (reasoning.Count > 0)
                    reasoningByIndex.Add(wireIndex, string.Concat(reasoning.Select(content => content.Text)));
                else if (
                    precedingReasoning != null
                    && message.RawRepresentation is not OpenAI.Chat.ChatMessage
                    && message.Contents.OfType<FunctionCallContent>().Any()
                )
                    reasoningByIndex.Add(wireIndex, precedingReasoning);

                // MAF 可能将推理与调用拆成相邻 Assistant 消息；只继承连续同作者的推理前缀，不能跨回合借用。
                // MAF may split reasoning and tool calls into adjacent assistant messages.
                // Echo only that contiguous reasoning prefix; never borrow from an earlier turn or author.
                reasoningOnly =
                    reasoning.Count > 0
                    && message.RawRepresentation is not OpenAI.Chat.ChatMessage
                    && message.Contents.All(content =>
                        content is TextReasoningContent or TextContent { Text.Length: 0 }
                    );
                if (reasoningOnly)
                {
                    precedingReasoning += string.Concat(reasoning.Select(content => content.Text));
                    precedingAuthor = message.AuthorName;
                }
            }
            if (!reasoningOnly)
                precedingReasoning = null;

            // 工具消息按结果展开后才是实际线协议索引；原生消息直接计数，未知角色不计入。
            // MEAI expands a tool message into one wire message per function result.
            // Native SDK messages bypass that conversion; unknown roles are omitted.
            if (message.RawRepresentation is OpenAI.Chat.ChatMessage)
                wireIndex++;
            else if (message.Role == ChatRole.Tool)
                wireIndex += message.Contents.OfType<FunctionResultContent>().Count();
            else if (
                message.Role == ChatRole.Assistant
                || message.Role == ChatRole.User
                || message.Role == ChatRole.System
                || message.Role.Value == "developer"
            )
                wireIndex++;
        }

        // 共享底层传输，但请求适配器与推理策略按调用创建，隔离并发请求和旧补丁。
        // The SDK transport is shared; only this lightweight adapter and its policy are
        // per request, so concurrent calls cannot share reasoning or retain old patches.
        var client = _client.AsIChatClient();
        if (reasoningByIndex.Count > 0 || _requiresReasoningContent)
#pragma warning disable MEAI001 // The SDK exposes request policies as an experimental extension seam.
            client
                .GetRequiredService<OpenAIRequestPolicies>()
                .AddPolicy(new ReasoningPolicy(reasoningByIndex, _requiresReasoningContent));
#pragma warning restore MEAI001
        return client;
    }

    /// <summary>
    /// <para>在 SDK 完成请求转换后，按线协议消息索引补充推理字段。</para>
    /// <para>Adds reasoning fields by wire-message index after the SDK has converted the request.</para>
    /// </summary>
    private sealed class ReasoningPolicy : PipelinePolicy
    {
        private readonly IReadOnlyDictionary<int, string> _reasoningByIndex;
        private readonly bool _requiresReasoningContent;

        /// <summary>
        /// <para>创建 ReasoningPolicy 实例并保存本包装层使用的依赖和配置。</para>
        /// <para>Initializes ReasoningPolicy with the dependencies and configuration used by this wrapper.</para>
        /// </summary>
        /// <param name="reasoningByIndex">
        /// <para>已转换请求中的消息索引到推理文本的映射。</para>
        /// <para>Mapping from converted request message indexes to reasoning text.</para>
        /// </param>
        /// <param name="requiresReasoningContent">
        /// <para>是否为缺少推理字段的 Assistant 消息补空字符串。</para>
        /// <para>Whether assistant messages missing reasoning fields require an empty string.</para>
        /// </param>
        public ReasoningPolicy(IReadOnlyDictionary<int, string> reasoningByIndex, bool requiresReasoningContent)
        {
            _reasoningByIndex = reasoningByIndex;
            _requiresReasoningContent = requiresReasoningContent;
        }

        /// <summary>
        /// <para>同步缓冲请求正文、补齐推理字段，再调用下一条 SDK 管道策略。</para>
        /// <para>Synchronously buffers the request body, patches reasoning fields, and invokes the next SDK pipeline policy.</para>
        /// </summary>
        /// <param name="message">
        /// <para>正在经过 SDK HTTP 管道的请求消息。</para>
        /// <para>Request message currently passing through the SDK HTTP pipeline.</para>
        /// </param>
        /// <param name="pipeline">
        /// <para>SDK 请求管道中的策略序列。</para>
        /// <para>Sequence of policies in the SDK request pipeline.</para>
        /// </param>
        /// <param name="currentIndex">
        /// <para>当前策略在管道中的位置，用于继续执行后续策略。</para>
        /// <para>Position of the current policy, used to continue the pipeline.</para>
        /// </param>
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            using var buffer = new MemoryStream();
            message.Request.Content!.WriteTo(buffer, message.CancellationToken);
            Apply(message, buffer);
            ProcessNext(message, pipeline, currentIndex);
        }

        /// <summary>
        /// <para>异步缓冲请求正文、补齐推理字段，再等待下一条 SDK 管道策略完成。</para>
        /// <para>Asynchronously buffers the request body, patches reasoning fields, and awaits the next SDK pipeline policy.</para>
        /// </summary>
        /// <param name="message">
        /// <para>正在经过 SDK HTTP 管道的请求消息。</para>
        /// <para>Request message currently passing through the SDK HTTP pipeline.</para>
        /// </param>
        /// <param name="pipeline">
        /// <para>SDK 请求管道中的策略序列。</para>
        /// <para>Sequence of policies in the SDK request pipeline.</para>
        /// </param>
        /// <param name="currentIndex">
        /// <para>当前策略在管道中的位置，用于继续执行后续策略。</para>
        /// <para>Position of the current policy, used to continue the pipeline.</para>
        /// </param>
        /// <returns>
        /// <para>表示上述异步处理完成的任务。</para>
        /// <para>Task representing completion of the asynchronous operation described above.</para>
        /// </returns>
        public override async ValueTask ProcessAsync(
            PipelineMessage message,
            IReadOnlyList<PipelinePolicy> pipeline,
            int currentIndex
        )
        {
            using var buffer = new MemoryStream();
            await message.Request.Content!.WriteToAsync(buffer, message.CancellationToken).ConfigureAwait(false);
            Apply(message, buffer);
            await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
        }

        /// <summary>
        /// <para>在已转换的 JSON 请求中写入已映射推理，并仅为 DeepSeek 缺失的字段补空值，保留其他请求内容。</para>
        /// <para>Writes mapped reasoning into converted request JSON and fills only missing DeepSeek fields with empty values, preserving other request content.</para>
        /// </summary>
        /// <param name="message">
        /// <para>正在经过 SDK HTTP 管道的请求消息。</para>
        /// <para>Request message currently passing through the SDK HTTP pipeline.</para>
        /// </param>
        /// <param name="buffer">
        /// <para>包含已序列化请求正文的内存缓冲区。</para>
        /// <para>Memory buffer containing the serialized request body.</para>
        /// </param>
        private void Apply(PipelineMessage message, MemoryStream buffer)
        {
            // 仅在 SDK 完成转换后补字段，不替换消息数组，以保留工具调用、多模态内容和调用选项。
            // Patch the fully converted request, preserving all SDK-generated content,
            // tool calls and caller options instead of replacing the messages array.
            var payload = JsonNode.Parse(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)))!;
            var messages = payload["messages"]!.AsArray();
            foreach (var (index, reasoning) in _reasoningByIndex)
                messages[index]!["reasoning_content"] = reasoning;
            if (_requiresReasoningContent)
            {
                // DeepSeek 对普通历史回答同样要求推理字段；缺失时补空字符串，并保留已有原生值。
                // DeepSeek requires this field on plain historical answers too, not only tool calls.
                // Empty means no recorded reasoning; preserve any native SDK reasoning already present.
                foreach (var input in messages)
                    if ((string?)input?["role"] == "assistant" && input["reasoning_content"] == null)
                        input["reasoning_content"] = string.Empty;
            }
            var original = message.Request.Content;
            message.Request.Content = BinaryContent.Create(BinaryData.FromObjectAsJson(payload));
            original?.Dispose();
        }
    }
}
