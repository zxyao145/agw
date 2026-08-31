using Agw.Agents.Execution.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.ExternalAgents.Pi;

/// <summary>
/// Adapts authoritative Pi response messages to Agw history semantics before delegating persistence.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <c>EfCoreChatHistoryProvider</c>, this provider does not own database access, conversation ordering, locking,
/// or history retrieval. It forwards those responsibilities to an inner provider.
/// </para>
/// <para>
/// The shared EF provider intentionally remains Agent-agnostic: it filters blank content during append and honors
/// exclusion metadata during reads. It already treats Agw <c>ToolMessageTypes</c> status records (todo, mode, background,
/// and warning updates) as UI-only and excludes them from model history and handoff. It still retains matched
/// <see cref="FunctionResultContent"/> values in <see cref="ChatRole.Tool"/> messages when a generic Function Call history
/// requires them. Pi owns that Tool history provider-side, so this adapter additionally marks Pi System, User, and Tool
/// records as display-only before delegation.
/// </para>
/// </remarks>
internal sealed class PiChatHistoryProvider : ChatHistoryProvider
{
    private readonly ChatHistoryProvider _innerProvider;

    /// <summary>Initializes the Pi history adapter over the supplied storage provider.</summary>
    /// <param name="innerProvider">
    /// The provider that owns history state, retrieval, and persistence; in Agw this is normally the shared EF provider.
    /// </param>
    public PiChatHistoryProvider(ChatHistoryProvider innerProvider)
    {
        _innerProvider = innerProvider;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Unlike an independent provider, this adapter reuses the EF provider's keys so it does not create a second set of
    /// conversation state entries in <see cref="AgentSession.StateBag"/>.
    /// </remarks>
    public override IReadOnlyList<string> StateKeys => _innerProvider.StateKeys;

    /// <inheritdoc />
    /// <remarks>
    /// Unlike <c>EfCoreChatHistoryProvider.ProvideChatHistoryAsync</c>, this method performs no database query or history
    /// filtering itself. It delegates the complete read path unchanged; Pi uses the call for provider state initialization
    /// but does not resend returned Agw history because Pi owns model-side conversation history.
    /// </remarks>
    protected override ValueTask<IEnumerable<ChatMessage>> InvokingCoreAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default
    ) => _innerProvider.InvokingAsync(context, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Unlike the generic EF write path, this boundary applies Pi role-based display-only metadata before persistence. It
    /// complements rather than replaces the EF provider's existing exclusion of Agw Tool status message types. SDK request
    /// callbacks are ignored because the shared request-context pipeline owns request persistence. Failed invocation
    /// contexts retain the original <see cref="ChatHistoryProvider.InvokedContext.InvokeException"/>.
    /// </remarks>
    protected override ValueTask InvokedCoreAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        // EF already excludes Agw Tool status types; keep Pi's additional role-based policy at the Pi adapter boundary.
        if (context.InvokeException != null)
        {
#pragma warning disable MAAI001
            var failedContext = new InvokedContext(context.Agent, context.Session, [], context.InvokeException);
#pragma warning restore MAAI001
            return _innerProvider.InvokedAsync(failedContext, cancellationToken);
        }

        var responseMessages = SanitizeMessages(context.ResponseMessages ?? []);
#pragma warning disable MAAI001
        var delegated = new InvokedContext(context.Agent, context.Session, [], responseMessages);
#pragma warning restore MAAI001
        return _innerProvider.InvokedAsync(delegated, cancellationToken);
    }

    /// <summary>Creates persistable Pi history messages and removes all transport-only representations.</summary>
    /// <remarks>
    /// The EF provider also removes blank textual content during append and its serializer ignores raw representations.
    /// Normalizing here still matters because it guarantees the same Pi contract for any inner provider and applies Pi's
    /// additional role-based display metadata before shared model-history and handoff consumers inspect the records.
    /// </remarks>
    /// <param name="messages">The request or authoritative response messages to normalize.</param>
    /// <returns>Nonblank cloned messages with Pi display-only metadata applied.</returns>
    private static List<ChatMessage> SanitizeMessages(IEnumerable<ChatMessage> messages)
    {
        var sanitized = messages
            .Select(ExternalAgentChatHistoryAgent.CreatePersistableMessage)
            .OfType<ChatMessage>()
            .ToList();
        foreach (var message in sanitized)
        {
            message.RawRepresentation = null;
            foreach (var content in message.Contents)
            {
                content.RawRepresentation = null;
            }
        }

        return sanitized;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Unlike a standalone provider, the adapter exposes services from the inner EF provider so callers can still discover
    /// its persistence and provider-session capabilities through the decorated instance.
    /// </remarks>
    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        base.GetService(serviceType, serviceKey) ?? _innerProvider.GetService(serviceType, serviceKey);
}
