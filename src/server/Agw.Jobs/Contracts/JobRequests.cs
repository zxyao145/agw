namespace Agw.Jobs.Contracts;

public sealed record JobScheduleDto
{
    public string? Kind { get; init; }
    public DateTimeOffset? At { get; init; }
    public string? Interval { get; init; }
    public string? Cron { get; init; }
}

public sealed record JobCreateRequest
{
    public string? Name { get; init; }
    public bool Enabled { get; init; } = true;
    public Guid ProjectId { get; init; }
    public AgentRuntimeType TargetKind { get; init; }
    public Guid AgentId { get; init; }
    public string? Instructions { get; init; }
    public JobScheduleDto? Schedule { get; init; }
}

public sealed record JobUpdateRequest
{
    public Guid Id { get; init; }
    public string? Name { get; init; }
    public bool Enabled { get; init; } = true;
    public Guid ProjectId { get; init; }
    public AgentRuntimeType TargetKind { get; init; }
    public Guid AgentId { get; init; }
    public string? Instructions { get; init; }
    public JobScheduleDto? Schedule { get; init; }
}

public sealed record JobEnableRequest
{
    public Guid Id { get; init; }
    public bool Enabled { get; init; }
}

public sealed record JobIdRequest
{
    public Guid Id { get; init; }
}
