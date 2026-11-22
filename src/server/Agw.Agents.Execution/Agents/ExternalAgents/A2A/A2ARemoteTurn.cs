using global::A2A;
using Agw.Agents.ExternalAgents.A2A;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.ExternalAgents.A2A;

internal sealed class A2ARemoteTurn
{
    private readonly Dictionary<string, ArtifactProgress> _artifacts = new(StringComparer.Ordinal);
    private readonly List<(string MessageId, string Text)> _artifactText = [];
    private readonly HashSet<string> _statusMessages = new(StringComparer.Ordinal);
    private string? _terminalText;
    private object? _terminalEvent;
    private string? _lastAssistantMessageId;

    public A2ARemoteTurn(A2ARemoteAgentSession session)
    {
        Session = session;
    }

    public A2ARemoteAgentSession Session { get; }
    public bool IsComplete { get; private set; }
    public A2AClientDiagnostics.TurnOutcome Outcome { get; set; }
    public int ResubscribeCount { get; set; }
    public string? ObservedTaskId { get; private set; }
    public bool HasActiveTask => ObservedTaskId is not null && IsActive(Session.TaskState);

    public static bool IsActive(TaskState? state) => state is TaskState.Submitted or TaskState.Working;

    public static bool IsAwaitingInput(TaskState? state) => state is TaskState.InputRequired or TaskState.AuthRequired;

    public IEnumerable<AgentResponseUpdate> Process(StreamResponse evt, bool recovered = false) =>
        evt.PayloadCase switch
        {
            StreamResponseCase.Message => ProcessMessage(evt.Message!),
            StreamResponseCase.Task => ProcessTask(evt.Task!, recovered: recovered),
            StreamResponseCase.ArtifactUpdate => ProcessArtifactUpdate(evt.ArtifactUpdate!),
            StreamResponseCase.StatusUpdate => ProcessStatus(
                evt.StatusUpdate!.ContextId,
                evt.StatusUpdate.TaskId,
                evt.StatusUpdate.Status,
                evt.StatusUpdate
            ),
            _ => throw new AgwException(
                ErrorCodes.A2ARemoteAgentRequestFailed,
                "The remote A2A agent returned an empty streaming event."
            ),
        };

    public IEnumerable<AgentResponseUpdate> ProcessMessage(Message message)
    {
        Session.RecordContext(message.ContextId);
        Session.ClearTask();
        Complete(message, message);
        if (MapMessage(message, message) is { } update)
        {
            yield return update;
        }
    }

    public IEnumerable<AgentResponseUpdate> ProcessTask(
        AgentTask task,
        bool includeArtifacts = true,
        bool recovered = false
    )
    {
        Session.RecordContext(task.ContextId);
        RecordTask(task.Id, task.Status.State);
        if (includeArtifacts)
        {
            var lastChunk = !IsActive(task.Status.State);
            foreach (var artifact in task.Artifacts ?? [])
            {
                var chunk = (Append: false, LastChunk: lastChunk);
                var update = MapArtifact(task.Id, artifact, chunk, task, recovered);
                if (update is not null)
                {
                    yield return update;
                }
            }
        }
        foreach (var update in ProcessStatus(task.ContextId, task.Id, task.Status, task))
        {
            yield return update;
        }
    }

    public AgentResponseUpdate? CreateResult()
    {
        var text =
            Session.TaskState == TaskState.Completed ? string.Concat(_artifactText.Select(chunk => chunk.Text)) : null;
        if (string.IsNullOrEmpty(text))
        {
            text = _terminalText;
        }
        return string.IsNullOrEmpty(text)
            ? null
            : new AgentResponseUpdate(ChatRole.Assistant, text)
            {
                MessageId = Guid.NewGuid().ToString("N"),
                RawRepresentation = _terminalEvent,
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    [AgwMessageClassifier.TypeKey] = AgwMessageClassifier.ResultType,
                    [AgwMessageClassifier.ResultSourceMessageIdKey] = _lastAssistantMessageId,
                },
            };
    }

    private IEnumerable<AgentResponseUpdate> ProcessArtifactUpdate(TaskArtifactUpdateEvent evt)
    {
        Session.RecordContext(evt.ContextId);
        var state =
            Session.TaskId == evt.TaskId && IsActive(Session.TaskState) ? Session.TaskState!.Value : TaskState.Working;
        RecordTask(evt.TaskId, state);
        var chunk = (Append: evt.Append == true, LastChunk: evt.LastChunk == true);
        if (MapArtifact(evt.TaskId, evt.Artifact, chunk, evt) is { } update)
        {
            yield return update;
        }
    }

    private IEnumerable<AgentResponseUpdate> ProcessStatus(
        string? contextId,
        string taskId,
        global::A2A.TaskStatus status,
        object raw
    )
    {
        Session.RecordContext(contextId);
        RecordTask(taskId, status.State);
        var message = status.Message;
        switch (status.State)
        {
            case var state when IsActive(state):
                if (message is null || !_statusMessages.Add(message.MessageId))
                    break;

                var reasoning = GetText(message.Parts);
                if (reasoning.Length > 0)
                {
                    yield return new AgentResponseUpdate(ChatRole.Assistant, [new TextReasoningContent(reasoning)])
                    {
                        MessageId = message.MessageId,
                        RawRepresentation = raw,
                    };
                }
                break;
            case TaskState.Completed:
            case var state when IsAwaitingInput(state):
                Complete(message, raw);
                if (message is not null && MapMessage(message, raw) is { } update)
                {
                    yield return update;
                }
                break;
            case TaskState.Failed or TaskState.Rejected or TaskState.Canceled:
                var text = message is null ? null : GetText(message.Parts);
                throw string.IsNullOrWhiteSpace(text)
                    ? new AgwException(ErrorCodes.A2ARemoteTaskFailed)
                    : new AgwException(ErrorCodes.A2ARemoteTaskFailed, text);
            default:
                throw new AgwException(
                    ErrorCodes.A2ARemoteAgentRequestFailed,
                    "The remote A2A agent returned an invalid task state."
                );
        }
    }

    private void RecordTask(string taskId, TaskState state)
    {
        ObservedTaskId = taskId;
        Session.RecordTask(taskId, state);
    }

    private AgentResponseUpdate? MapArtifact(
        string taskId,
        Artifact artifact,
        (bool Append, bool LastChunk) chunk,
        object raw,
        bool recovered = false
    )
    {
        var artifactId = $"{taskId}:{artifact.ArtifactId}";
        var previous = _artifacts.GetValueOrDefault(artifactId);
        if (previous is { Complete: true } && (recovered || previous.Subscription != ResubscribeCount))
        {
            return null;
        }

        var version = previous is null ? (recovered ? 1 : 0) : (chunk.Append ? previous.Version : previous.Version + 1);
        var messageId = version == 0 ? artifactId : $"{artifactId}#{version}";
        if (recovered && previous is not null)
        {
            _artifactText.RemoveAll(chunk => chunk.MessageId == previous.MessageId);
        }
        _artifacts[artifactId] = new ArtifactProgress
        {
            MessageId = messageId,
            Version = version,
            Complete = chunk.LastChunk,
            Subscription = ResubscribeCount,
        };
        _artifactText.Add((messageId, GetText(artifact.Parts)));
        return MapContents(messageId, artifact.Parts, raw);
    }

    private AgentResponseUpdate? MapMessage(Message message, object raw) =>
        MapContents(message.MessageId, message.Parts, raw);

    private AgentResponseUpdate? MapContents(string messageId, IReadOnlyList<Part> parts, object raw)
    {
        if (parts.Count == 0)
        {
            return null;
        }
        List<AIContent> contents;
        try
        {
            contents = parts.Select(part => part.ToAIContent()).ToList();
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException)
        {
            throw new AgwException(
                ErrorCodes.A2ARemoteAgentRequestFailed,
                "The remote A2A agent returned invalid content.",
                A2ARequestErrors.SafeException(exception)
            );
        }

        _lastAssistantMessageId = messageId;
        return new AgentResponseUpdate(ChatRole.Assistant, contents) { MessageId = messageId, RawRepresentation = raw };
    }

    private void Complete(Message? message, object raw)
    {
        IsComplete = true;
        _terminalText = message is not null ? GetText(message.Parts) : null;
        _terminalEvent = raw;
    }

    private static string GetText(IEnumerable<Part> parts) => string.Concat(parts.Select(part => part.Text));

    private sealed class ArtifactProgress
    {
        public required string MessageId { get; init; }
        public required int Version { get; init; }
        public required bool Complete { get; init; }
        public required int Subscription { get; init; }
    }
}
