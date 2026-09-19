using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

#pragma warning disable OPENAI001 // Responses API types are marked experimental by the SDK.
#pragma warning disable SCME0001 // DeepSeek's reasoning.content is exposed through JsonPatch.

namespace Agw.Agents.Execution.Agents.Middleware;

/// <summary>Projects portable reasoning into DeepSeek's plain-text Responses protocol.</summary>
internal sealed class OpenAiResponsesReasoningChatClient : DelegatingChatClient
{
    private OpenAiResponsesReasoningChatClient(IChatClient innerClient)
        : base(innerClient) { }

    public static IChatClient Create(IChatClient client, Uri endpoint) =>
        endpoint.Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase)
            ? new OpenAiResponsesReasoningChatClient(client)
            : client;

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

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    ) => base.GetStreamingResponseAsync(ProjectReasoning(messages), options, cancellationToken);

    private static IEnumerable<ChatMessage> ProjectReasoning(IEnumerable<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            if (message.Role != ChatRole.Assistant || !message.Contents.OfType<TextReasoningContent>().Any())
            {
                yield return message;
                continue;
            }

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
