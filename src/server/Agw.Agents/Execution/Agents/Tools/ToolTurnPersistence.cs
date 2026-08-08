using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.Tools;

internal sealed class ToolTurnPersistence
{
    private readonly AIAgent _agent;
    private readonly AgentSession _session;
    private readonly Func<IReadOnlyList<ChatMessage>, CancellationToken, Task> _persistAsync;
    private readonly List<ChatMessage> _responseMessages = [];
    private IReadOnlyList<ChatMessage> _stateSnapshots = [];
    private int _completionAttempted;

    public ToolTurnPersistence(
        AIAgent agent,
        AgentSession session,
        Func<IReadOnlyList<ChatMessage>, CancellationToken, Task> persistAsync)
    {
        _agent = agent;
        _session = session;
        _persistAsync = persistAsync;
    }

    public IReadOnlyList<ChatMessage> ResponseMessages => _responseMessages;

    public bool CompletionAttempted => Volatile.Read(ref _completionAttempted) != 0;

    public void Record(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _responseMessages.Add(message);
    }

    public void RecordRange(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        _responseMessages.AddRange(messages);
    }

    public async Task<IReadOnlyList<ChatMessage>> CompleteAsync(
        CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _completionAttempted, 1) != 0)
        {
            return _stateSnapshots;
        }

        _stateSnapshots = await ToolStateSnapshots
            .CreateAsync(_agent, _session, _responseMessages, cancellationToken)
            .ConfigureAwait(false);
        var messages = _responseMessages
            .Where(ToolStateSnapshots.RequiresSeparatePersistence)
            .Concat(_stateSnapshots)
            .ToList();
        await _persistAsync(messages, cancellationToken).ConfigureAwait(false);
        return _stateSnapshots;
    }
}
