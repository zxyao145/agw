using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.Middleware.ModelInput;

internal sealed class OpenAiResponsesPromptCachingChatClient : DelegatingChatClient
{
    private static readonly ChatRole DeveloperRole = new("developer");
    private readonly string _defaultModelId;
    private readonly string? _stableInstructions;

    private OpenAiResponsesPromptCachingChatClient(
        IChatClient innerClient,
        string defaultModelId,
        string? stableInstructions
    )
        : base(innerClient)
    {
        _defaultModelId = defaultModelId;
        _stableInstructions = stableInstructions;
    }

    public static IChatClient Create(IChatClient client, Uri endpoint, string modelId, string? stableInstructions) =>
        IsSupportedEndpoint(endpoint) && !string.IsNullOrWhiteSpace(stableInstructions)
            ? new OpenAiResponsesPromptCachingChatClient(client, modelId, stableInstructions)
            : client;

    public static IChatClient CreateForSummary(IChatClient client, Uri endpoint, string modelId) =>
        IsSupportedEndpoint(endpoint)
            ? new OpenAiResponsesPromptCachingChatClient(client, modelId, stableInstructions: null)
            : client;

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var request = PrepareRequest(messages, options);
        return base.GetResponseAsync(request.Messages, request.Options, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var request = PrepareRequest(messages, options);
        return base.GetStreamingResponseAsync(request.Messages, request.Options, cancellationToken);
    }

    private (IEnumerable<ChatMessage> Messages, ChatOptions? Options) PrepareRequest(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options
    )
    {
        if (
            !IsGptVersionAtLeast(options?.ModelId ?? _defaultModelId, 6, 2)
            || options?.ConversationId is not null
            || options?.ContinuationToken is not null
            || options?.RawRepresentationFactory is not null
        )
        {
            return (messages, options);
        }

        if (_stableInstructions is null)
        {
            var summaryMessages = messages.ToList();
            if (summaryMessages.FirstOrDefault()?.Role == ChatRole.System)
            {
                summaryMessages[0] = AsDeveloperMessage(summaryMessages[0]);
            }

            return (summaryMessages, options);
        }

        if (!TryGetRequestInstructions(options?.Instructions, _stableInstructions, out var requestInstructions))
        {
            return (messages, options);
        }

        var preparedMessages = messages.ToList();
        if (preparedMessages.FirstOrDefault() is not { } first || !IsStableInstructionMessage(first))
        {
            preparedMessages.Insert(0, new ChatMessage(DeveloperRole, _stableInstructions));
        }
        else if (first.Role == ChatRole.System)
        {
            preparedMessages[0] = AsDeveloperMessage(first);
        }

        var preparedOptions = options!.Clone();
        preparedOptions.Instructions = requestInstructions;
        return (preparedMessages, preparedOptions);
    }

    private static bool TryGetRequestInstructions(
        string? instructions,
        string stableInstructions,
        out string? requestInstructions
    )
    {
        requestInstructions = null;
        if (instructions is null || !instructions.StartsWith(stableInstructions, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = instructions.AsSpan(stableInstructions.Length);
        if (suffix.IsEmpty)
        {
            return true;
        }

        if (suffix.StartsWith("\r\n", StringComparison.Ordinal))
        {
            requestInstructions = suffix[2..].ToString();
            return true;
        }

        if (suffix[0] == '\n')
        {
            requestInstructions = suffix[1..].ToString();
            return true;
        }

        return false;
    }

    private bool IsStableInstructionMessage(ChatMessage message) =>
        (message.Role == ChatRole.System || message.Role == DeveloperRole)
        && string.Concat(message.Contents.OfType<TextContent>().Select(content => content.Text)) == _stableInstructions;

    private static ChatMessage AsDeveloperMessage(ChatMessage message)
    {
        var copy = message.Clone();
        copy.Role = DeveloperRole;
        return copy;
    }

    private static bool IsSupportedEndpoint(Uri endpoint) =>
        endpoint.Scheme == Uri.UriSchemeHttps
        && endpoint.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsGptVersionAtLeast(string modelId, int requiredMajor, int requiredMinor)
    {
        if (!modelId.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var modelVersion = modelId[4..].Split('-', 2)[0].Split('.', 2);
        if (!int.TryParse(modelVersion[0], out var major))
        {
            return false;
        }

        var minor = modelVersion.Length > 1 && int.TryParse(modelVersion[1], out var parsedMinor) ? parsedMinor : 0;
        return major > requiredMajor || (major == requiredMajor && minor >= requiredMinor);
    }
}
