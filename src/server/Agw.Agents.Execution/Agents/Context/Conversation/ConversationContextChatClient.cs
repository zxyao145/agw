using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Agw.Agents.Execution.Agents.History;
using Agw.Agents.Execution.Agents.Middleware.History;
using Agw.Shared.Exceptions;
using Agw.Tools.Runtime;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.Context.Conversation;

internal sealed class ConversationContextChatClient : DelegatingChatClient
{
    internal const string StateKey = "agw.conversationContext";

    private static readonly ProviderSessionState<ConversationContextState> SessionState = new(
        _ => new ConversationContextState(false, null, null),
        StateKey,
        new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() }
    );

    private readonly IReadOnlyList<IConversationContextSource> _contextSources;
    private readonly IReadOnlyList<IContextAttachmentSource> _attachmentSources;
    private readonly AgwChatHistoryProvider? _attachmentHistory;

    public ConversationContextChatClient(
        IChatClient innerClient,
        IReadOnlyList<IConversationContextSource> contextSources,
        IReadOnlyList<IContextAttachmentSource> attachmentSources,
        AgwChatHistoryProvider? attachmentHistory
    )
        : base(innerClient)
    {
        _contextSources = contextSources;
        _attachmentSources = attachmentSources;
        if (attachmentSources.Count > 0)
            _attachmentHistory =
                attachmentHistory
                ?? throw new AgwException(
                    ErrorCodes.AgentExecutionFailed,
                    "Context attachment sources require AgwChatHistoryProvider."
                );
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var request = await PrepareMessagesAsync(messages, cancellationToken).ConfigureAwait(false);
        return await base.GetResponseAsync(request, options, cancellationToken).ConfigureAwait(false);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var request = await PrepareMessagesAsync(messages, cancellationToken).ConfigureAwait(false);
        await foreach (
            var update in base.GetStreamingResponseAsync(request, options, cancellationToken).ConfigureAwait(false)
        )
            yield return update;
    }

    private async ValueTask<List<ChatMessage>> PrepareMessagesAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken
    )
    {
        var session =
            LocalHistoryCompactionScopeChatClient.GetOriginalSession(AIAgent.CurrentRunContext?.Session)
            ?? throw new AgwException(
                ErrorCodes.AgentExecutionFailed,
                "Conversation context requires an Agent session."
            );
        var request = messages.ToList();
        var contextText =
            _contextSources.Count == 0
                ? null
                : await GetContextTextAsync(session, request.FirstOrDefault()?.MessageId, cancellationToken)
                    .ConfigureAwait(false);
        if (_attachmentHistory is { } history)
        {
            var attachments = await GetAttachmentsAsync(session, request, cancellationToken).ConfigureAwait(false);
            if (attachments.Count > 0)
            {
                await history
                    .WriteContextAttachmentsAsync(session, attachments, cancellationToken)
                    .ConfigureAwait(false);
                request.AddRange(attachments);
            }
        }

        if (contextText is not null)
            request.Insert(0, new ChatMessage(ChatRole.User, contextText));
        return request;
    }

    private async ValueTask<string?> GetContextTextAsync(
        AgentSession session,
        string? anchorMessageId,
        CancellationToken cancellationToken
    )
    {
        var state = SessionState.GetOrInitializeState(session);
        if (state.Computed && state.AnchorMessageId == anchorMessageId)
            return state.Text;

        var sections = new List<(string Title, string Content)>();
        foreach (var source in _contextSources)
        {
            var content = await source.GetContentAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(content))
                sections.Add((source.Title, content));
        }

        state = new ConversationContextState(true, Format(sections), anchorMessageId);
        SessionState.SaveState(session, state);
        return state.Text;
    }

    private async ValueTask<List<ChatMessage>> GetAttachmentsAsync(
        AgentSession session,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken
    )
    {
        var attachments = new List<ChatMessage>();
        foreach (var source in _attachmentSources)
        {
            var attachment = await source.EvaluateAsync(session, messages, cancellationToken).ConfigureAwait(false);
            if (attachment is null)
                continue;

            var message = new ChatMessage(ChatRole.User, WrapReminder(attachment.Text))
            {
                MessageId = Guid.CreateVersion7().ToString("D"),
            };
            message = message.WithAgentRequestMessageSource(
                AgentRequestMessageSourceType.AIContextProvider,
                attachment.Type
            );
            ContextAttachmentHistory.SetAttachmentType(message, attachment.Type);
            attachments.Add(message);
        }
        return attachments;
    }

    internal static string? Format(IReadOnlyList<(string Title, string Content)> sections) =>
        sections.Count == 0
            ? null
            : WrapReminder(
                "Context for this conversation.\nUse it when it is relevant to the request.\n\n"
                    + string.Join(
                        "\n\n",
                        sections.Select(section => $"# {section.Title}\n{section.Content.TrimEnd('\r', '\n')}")
                    )
            );

    private static string WrapReminder(string text) => $"<system-reminder>\n{text}\n</system-reminder>";
}

internal sealed record ConversationContextState(bool Computed, string? Text, string? AnchorMessageId);
