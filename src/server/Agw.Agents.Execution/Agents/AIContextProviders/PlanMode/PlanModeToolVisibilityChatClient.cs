using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.AIContextProviders.PlanMode;

internal sealed class PlanModeToolVisibilityChatClient : DelegatingChatClient
{
    public PlanModeToolVisibilityChatClient(IChatClient innerClient)
        : base(innerClient) { }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    ) => base.GetResponseAsync(messages, HideRestrictedTools(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    ) => base.GetStreamingResponseAsync(messages, HideRestrictedTools(options), cancellationToken);

    private static ChatOptions? HideRestrictedTools(ChatOptions? options)
    {
        if (
            options?.Tools == null
            || !options.Tools.Any(static tool => tool is PlanModeRestrictedAIFunction { HideFromModel: true })
        )
        {
            return options;
        }

        var filteredOptions = options.Clone();
        filteredOptions.Tools = options
            .Tools.Where(static tool => tool is not PlanModeRestrictedAIFunction { HideFromModel: true })
            .ToArray();
        return filteredOptions;
    }
}
