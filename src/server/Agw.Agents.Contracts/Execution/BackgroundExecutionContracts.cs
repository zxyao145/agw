using Agw.Agents.Contracts.Messages;

namespace Agw.Agents.Contracts.Execution;

public interface IBackgroundExecutionFacade
{
    Task<BackgroundExecutionReceipt> SubmitAsync(
        BackgroundExecutionRequest request,
        CancellationToken cancellationToken
    );

    Task<BackgroundExecutionStatus> GetAsync(Guid turnId, CancellationToken cancellationToken);

    IAsyncEnumerable<AgentExecutionEvent> SubscribeAsync(
        Guid turnId,
        string? afterCursor,
        CancellationToken cancellationToken
    );

    Task<bool> CancelAsync(BackgroundExecutionCancelRequest request, CancellationToken cancellationToken);
}

public sealed record BackgroundExecutionCancelRequest
{
    public BackgroundExecutionCancelRequest(Guid turnId, string correlationKey, Guid? parentEventId, string reason)
    {
        TurnId = turnId;
        CorrelationKey = correlationKey;
        ParentEventId = parentEventId;
        Reason = reason;
    }

    public Guid TurnId { get; init; }

    public string CorrelationKey { get; init; }

    public Guid? ParentEventId { get; init; }

    public string Reason { get; init; }
}

public sealed record BackgroundExecutionRequest
{
    public BackgroundExecutionRequest(
        Guid turnId,
        AgentTarget target,
        Guid projectId,
        Guid? conversationId,
        string title,
        AgwUserInput input,
        AgentExecutionPermissionMode permissionMode,
        BackgroundToolPolicy toolPolicy,
        string correlationKey,
        Guid? parentEventId
    )
    {
        TurnId = turnId;
        Target = target;
        ProjectId = projectId;
        ConversationId = conversationId;
        Title = title;
        Input = input;
        PermissionMode = permissionMode;
        ToolPolicy = toolPolicy;
        CorrelationKey = correlationKey;
        ParentEventId = parentEventId;
    }

    public Guid TurnId { get; init; }

    public AgentTarget Target { get; init; }

    public Guid ProjectId { get; init; }

    public Guid? ConversationId { get; init; }

    public string Title { get; init; }

    public AgwUserInput Input { get; init; }

    public AgentExecutionPermissionMode PermissionMode { get; init; }

    public BackgroundToolPolicy ToolPolicy { get; init; }

    public string CorrelationKey { get; init; }

    public Guid? ParentEventId { get; init; }
}

public sealed record BackgroundExecutionReceipt(Guid TurnId, Guid ConversationId, AgentExecutionState State);

public sealed record BackgroundExecutionStatus
{
    public BackgroundExecutionStatus(
        Guid turnId,
        string callerKey,
        string correlationKey,
        AgentExecutionState state,
        string? resultText,
        string? error,
        long inputTokens,
        long outputTokens
    )
    {
        TurnId = turnId;
        CallerKey = callerKey;
        CorrelationKey = correlationKey;
        State = state;
        ResultText = resultText;
        Error = error;
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
    }

    public Guid TurnId { get; init; }

    public string CallerKey { get; init; }

    public string CorrelationKey { get; init; }

    public AgentExecutionState State { get; init; }

    public string? ResultText { get; init; }

    public string? Error { get; init; }

    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }
}
