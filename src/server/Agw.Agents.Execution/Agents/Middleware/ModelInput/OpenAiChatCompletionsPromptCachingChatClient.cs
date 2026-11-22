using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

#pragma warning disable SCME0001 // 通过 JsonPatch 设置 prompt_cache_key。Uses JsonPatch to set prompt_cache_key.

namespace Agw.Agents.Execution.Agents.Middleware.ModelInput;

internal sealed class OpenAiChatCompletionsPromptCachingChatClient : DelegatingChatClient
{
    private readonly Uri _endpoint;
    private readonly string _defaultModelId;
    private readonly string? _stableInstructions;

    private OpenAiChatCompletionsPromptCachingChatClient(
        IChatClient innerClient,
        Uri endpoint,
        string defaultModelId,
        string? stableInstructions
    )
        : base(innerClient)
    {
        _endpoint = endpoint;
        _defaultModelId = defaultModelId;
        _stableInstructions = stableInstructions;
    }

    public static IChatClient Create(
        IChatClient innerClient,
        Uri endpoint,
        string modelId,
        string? stableInstructions
    ) => new OpenAiChatCompletionsPromptCachingChatClient(innerClient, endpoint, modelId, stableInstructions);

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var inputMessages = messages as IList<ChatMessage> ?? messages.ToList();
        return base.GetResponseAsync(inputMessages, PrepareOptions(inputMessages, options), cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var inputMessages = messages as IList<ChatMessage> ?? messages.ToList();
        return base.GetStreamingResponseAsync(inputMessages, PrepareOptions(inputMessages, options), cancellationToken);
    }

    private ChatOptions? PrepareOptions(IList<ChatMessage> messages, ChatOptions? options)
    {
        var sortedTools = options?.Tools is { Count: > 1 } tools
            ? tools.OrderBy(tool => tool.Name, StringComparer.Ordinal).ToList()
            : null;
        var cacheKey = GetPromptCacheKey(messages, options, sortedTools ?? options?.Tools);
        if (sortedTools is null && cacheKey is null)
        {
            return options;
        }

        var preparedOptions = options?.Clone() ?? new ChatOptions();
        if (sortedTools is not null)
        {
            preparedOptions.Tools = sortedTools;
        }

        if (cacheKey is not null)
        {
            preparedOptions.RawRepresentationFactory = _ =>
            {
                var rawOptions = new ChatCompletionOptions();
                rawOptions.Patch.Set("$.prompt_cache_key"u8, cacheKey);
                return rawOptions;
            };
        }

        return preparedOptions;
    }

    private static bool SupportsPromptCacheKey(Uri endpoint, string modelId) =>
        endpoint.Scheme == Uri.UriSchemeHttps
        && endpoint.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase)
        && (
            IsModelFamily(modelId, "gpt-4o")
            || IsModelFamily(modelId, "gpt-4.1")
            || IsModelFamily(modelId, "gpt-5")
            || IsModelFamily(modelId, "gpt-5.1")
            || IsModelFamily(modelId, "gpt-5.2")
            || IsModelFamily(modelId, "gpt-5.4")
            || IsModelFamily(modelId, "gpt-5.5")
            || IsModelFamily(modelId, "o1")
            || IsModelFamily(modelId, "o3")
            || IsModelFamily(modelId, "o4-mini")
        );

    private static bool IsModelFamily(string modelId, string family) =>
        modelId.Equals(family, StringComparison.OrdinalIgnoreCase)
        || modelId.StartsWith($"{family}-", StringComparison.OrdinalIgnoreCase);

    private static string GetLeadingInstructions(IList<ChatMessage> messages) =>
        string.Join(
            "\n",
            messages
                .TakeWhile(message => message.Role == ChatRole.System || message.Role.Value == "developer")
                .Select(message =>
                    string.Concat(message.Contents.OfType<TextContent>().Select(content => content.Text))
                )
                .Where(instructions => instructions.Length > 0)
        );

    private static bool HasStableInstructionsPrefix(
        string? requestInstructions,
        IList<ChatMessage> messages,
        string stableInstructions
    ) =>
        HasStableInstructionsPrefix(requestInstructions, stableInstructions)
        || HasStableInstructionsPrefix(GetLeadingInstructions(messages), stableInstructions);

    private static bool HasStableInstructionsPrefix(string? instructions, string stableInstructions)
    {
        if (instructions is null || !instructions.StartsWith(stableInstructions, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = instructions.AsSpan(stableInstructions.Length);
        return suffix.IsEmpty || suffix[0] == '\n' || suffix.StartsWith("\r\n", StringComparison.Ordinal);
    }

    private string? GetPromptCacheKey(IList<ChatMessage> messages, ChatOptions? options, IList<AITool>? tools)
    {
        if (
            options?.RawRepresentationFactory is not null
            || !SupportsPromptCacheKey(_endpoint, options?.ModelId ?? _defaultModelId)
        )
        {
            return null;
        }
        if (options?.AdditionalProperties?.TryGetValue("prompt_cache_key", out var explicitKey) == true)
        {
            return explicitKey as string;
        }

        var instructions = _stableInstructions;
        if (instructions is null)
        {
            instructions = GetLeadingInstructions(messages);
        }
        else if (!HasStableInstructionsPrefix(options?.Instructions, messages, instructions))
        {
            instructions = null;
        }
        if (string.IsNullOrWhiteSpace(instructions) && tools is not { Count: > 0 })
        {
            return null;
        }

        var keyMaterial = new StringBuilder("agw-openai-chat-completions-cache-v1;");
        AppendKeyPart(keyMaterial, instructions ?? string.Empty);
        foreach (var tool in tools ?? [])
        {
            AppendKeyPart(keyMaterial, tool.Name);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial.ToString()))).ToLowerInvariant();
    }

    private static void AppendKeyPart(StringBuilder builder, string value) =>
        builder.Append(value.Length).Append(':').Append(value).Append(';');
}

#pragma warning restore SCME0001
