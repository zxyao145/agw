using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Agw.Tools.HumanInteraction;

/// <summary>Describes approval requests using the actual tools supplied to this model call.</summary>
public sealed class HumanInteractionDescriptionChatClient : DelegatingChatClient
{
    private readonly IHumanInteractionContextAccessor? _interactions;

    public HumanInteractionDescriptionChatClient(
        IChatClient innerClient,
        IHumanInteractionContextAccessor? interactions
    )
        : base(innerClient)
    {
        _interactions = interactions;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var message in response.Messages)
            Describe(message.Contents, options);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        await foreach (
            var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false)
        )
        {
            Describe(update.Contents, options);
            yield return update;
        }
    }

    private void Describe(IEnumerable<AIContent> contents, ChatOptions? options)
    {
        foreach (var approval in contents.OfType<ToolApprovalRequestContent>())
        {
            if (approval.ToolCall is not FunctionCallContent call)
                continue;
            var tool = options?.Tools?.OfType<AIFunction>().SingleOrDefault(function => function.Name == call.Name);
            if (tool?.GetService<IHumanInteractionProtocol>() is not { } protocol)
                continue;
            var description = protocol.CreateRequest(
                new AIFunctionArguments(call.Arguments ?? new Dictionary<string, object?>())
            ) with
            {
                Source = HumanInteractionToolMetadata.ReadSource(options) with
                {
                    ToolName = call.Name,
                    CallId = call.CallId,
                    ProviderRequestId = approval.RequestId,
                },
            };
            description = description with
            {
                Arguments = description.Arguments ?? JsonSerializer.SerializeToElement(call.Arguments),
            };
            _interactions?.Requests?.Register(approval.RequestId, description);
            HumanInteractionToolMetadata.Write(approval, description);
        }
    }
}
