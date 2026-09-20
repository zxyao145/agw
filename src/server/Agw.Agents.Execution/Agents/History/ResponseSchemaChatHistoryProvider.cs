using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.History;

/// <summary>Preserves the execution's structured Result format before SDK callbacks persist history.</summary>
internal sealed class ResponseSchemaChatHistoryProvider : ChatHistoryProvider
{
    private readonly ChatHistoryProvider _innerProvider;

    internal ResponseSchemaChatHistoryProvider(ChatHistoryProvider innerProvider)
    {
        _innerProvider = innerProvider;
    }

    public override IReadOnlyList<string> StateKeys => _innerProvider.StateKeys;

    protected override ValueTask<IEnumerable<ChatMessage>> InvokingCoreAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default
    ) => _innerProvider.InvokingAsync(context, cancellationToken);

    protected override ValueTask InvokedCoreAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        foreach (var message in context.ResponseMessages ?? [])
        {
            ResponseSchemaResultMetadata.Apply(message);
        }

        return _innerProvider.InvokedAsync(context, cancellationToken);
    }

    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        base.GetService(serviceType, serviceKey) ?? _innerProvider.GetService(serviceType, serviceKey);
}
