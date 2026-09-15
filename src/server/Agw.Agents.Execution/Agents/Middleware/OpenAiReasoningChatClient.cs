using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agw.Agents.Execution.Agents.Middleware;

/// <summary>
/// Returns the reasoning_content read by MEAI to compatible Chat Completions endpoints.
/// </summary>
internal sealed class OpenAiReasoningChatClient : DelegatingChatClient
{
    private readonly ChatClient _client;
    private readonly bool _requiresReasoningContent;

    private OpenAiReasoningChatClient(ChatClient client, bool requiresReasoningContent)
        : base(client.AsIChatClient())
    {
        _client = client;
        _requiresReasoningContent = requiresReasoningContent;
    }

    public static IChatClient Create(ChatClient client, Uri endpoint) =>
        // Native OpenAI uses its own reasoning protocol. Do not send a vendor extension
        // there when portable history originated from another model.
        endpoint.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase)
        || endpoint.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase)
            ? client.AsIChatClient()
            : new OpenAiReasoningChatClient(
                client,
                endpoint.Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase)
            );

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

    private IChatClient CreateRequestClient(IList<ChatMessage> messages, ChatOptions? options)
    {
        var reasoningByIndex = new Dictionary<int, string>();
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

    private sealed class ReasoningPolicy : PipelinePolicy
    {
        private readonly IReadOnlyDictionary<int, string> _reasoningByIndex;
        private readonly bool _requiresReasoningContent;

        public ReasoningPolicy(IReadOnlyDictionary<int, string> reasoningByIndex, bool requiresReasoningContent)
        {
            _reasoningByIndex = reasoningByIndex;
            _requiresReasoningContent = requiresReasoningContent;
        }

        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            using var buffer = new MemoryStream();
            message.Request.Content!.WriteTo(buffer, message.CancellationToken);
            Apply(message, buffer);
            ProcessNext(message, pipeline, currentIndex);
        }

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

        private void Apply(PipelineMessage message, MemoryStream buffer)
        {
            // Patch the fully converted request, preserving all SDK-generated content,
            // tool calls and caller options instead of replacing the messages array.
            var payload = JsonNode.Parse(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)))!;
            var messages = payload["messages"]!.AsArray();
            foreach (var (index, reasoning) in _reasoningByIndex)
                messages[index]!["reasoning_content"] = reasoning;
            if (_requiresReasoningContent)
            {
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
