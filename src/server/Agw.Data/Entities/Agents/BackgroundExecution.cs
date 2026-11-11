using System.ComponentModel.DataAnnotations.Schema;
using Agw.Agents.Contracts.Execution;
using Agw.Shared.Data.Encryption;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Agents;

[Table("background_execution")]
[EntityTypeConfiguration(typeof(BackgroundExecutionConfiguration))]
public sealed class BackgroundExecution : BaseEntity
{
    public Guid Id { get; set; }

    public string CallerKey { get; set; } = string.Empty;

    public string CorrelationKey { get; set; } = string.Empty;

    public Guid? ProjectId { get; set; }

    public Guid? ConversationId { get; set; }

    public AgentTargetKind? TargetKind { get; set; }

    public Guid? TargetId { get; set; }

    public BackgroundToolPolicy ToolPolicy { get; set; }

    public AgentExecutionState State { get; set; } = AgentExecutionState.Queued;

    [Encrypted]
    public string? ResultText { get; set; }

    [Encrypted]
    public string? Error { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string? RequestHash { get; set; }

    public Guid ChainId { get; set; }

    public Guid? ParentEventId { get; set; }

    public int Hops { get; set; }
}
