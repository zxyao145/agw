using System.Runtime.CompilerServices;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.Middleware;

/// <summary>Preserves Anthropic thinking block types when replaying portable history.</summary>
internal sealed class AnthropicReasoningChatClient : DelegatingChatClient
{
    private const string ThinkingTypeProperty = "agw.anthropic.thinkingType";
    private readonly bool _requiresThinking;

    private AnthropicReasoningChatClient(IChatClient innerClient, bool requiresThinking)
        : base(innerClient)
    {
        _requiresThinking = requiresThinking;
    }

    public static IChatClient Create(IChatClient client, Uri endpoint) =>
        new AnthropicReasoningChatClient(
            client,
            endpoint.Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase)
        );

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
            // Stamp every fragment before MEAI coalesces it and discards RawRepresentation.
            PreserveThinkingType(update.Contents);
            yield return update;
        }
    }

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
            // DeepSeek requires thinking on older plain answers too. Never substitute an
            // earlier turn's reasoning or overwrite a block supplied by the model/caller.
            if (_requiresThinking && !hasThinking)
                contents.Insert(0, EmptyThinking());

            var copy = message.Clone();
            copy.Contents = contents;
            yield return copy;
        }
    }

    private AIContent PreserveThinkingBlock(AIContent content)
    {
        if (content is not TextReasoningContent reasoning || content.RawRepresentation is ContentBlockParam)
            return content;

        var type = GetThinkingType(reasoning);
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

    private static void PreserveThinkingType(IEnumerable<AIContent> contents)
    {
        foreach (var reasoning in contents.OfType<TextReasoningContent>())
        {
            var type = GetThinkingType(reasoning);
            if (type != null)
                (reasoning.AdditionalProperties ??= [])[ThinkingTypeProperty] = type;
        }
    }

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

    private static TextReasoningContent EmptyThinking() =>
        new(string.Empty)
        {
            RawRepresentation = new ContentBlockParam(
                new ThinkingBlockParam { Thinking = string.Empty, Signature = string.Empty }
            ),
        };
}
