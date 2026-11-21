using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.Middleware.ModelInput;

internal sealed class AnthropicPromptCachingChatClient : DelegatingChatClient
{
    private const int DefaultMaxTokens = 1024;
    private readonly string _defaultModelId;

    private AnthropicPromptCachingChatClient(IChatClient innerClient, string defaultModelId)
        : base(innerClient)
    {
        _defaultModelId = defaultModelId;
    }

    public static IChatClient Create(IAnthropicClient client, string modelId) =>
        new AnthropicPromptCachingChatClient(client.AsIChatClient(modelId, DefaultMaxTokens), modelId);

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    ) => base.GetResponseAsync(messages, WithPromptCaching(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    ) => base.GetStreamingResponseAsync(messages, WithPromptCaching(options), cancellationToken);

    private ChatOptions WithPromptCaching(ChatOptions? options)
    {
        var requestOptions = options?.Clone() ?? new ChatOptions();
        var rawRepresentationFactory = requestOptions.RawRepresentationFactory;
        requestOptions.RawRepresentationFactory = client =>
        {
            var messageParams = rawRepresentationFactory?.Invoke(client) as MessageCreateParams;
            return messageParams is null
                ? new MessageCreateParams
                {
                    CacheControl = new CacheControlEphemeral(),
                    MaxTokens = requestOptions.MaxOutputTokens ?? DefaultMaxTokens,
                    Messages = [],
                    Model = requestOptions.ModelId ?? _defaultModelId,
                }
                : messageParams with
                {
                    CacheControl = new CacheControlEphemeral(),
                };
        };
        return requestOptions;
    }
}
