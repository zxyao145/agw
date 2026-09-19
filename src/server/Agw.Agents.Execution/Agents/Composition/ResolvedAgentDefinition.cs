using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.Composition;

public sealed class ResolvedAgentDefinition
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public string? SystemPrompt { get; init; }

    public required string ModelId { get; init; }

    public required string OpenTelemetrySourceName { get; init; }

    public required ChatHistoryProvider ChatHistoryProvider { get; init; }

    public AIContextProvider? CompactionProvider { get; init; }

    public int? MaxOutputTokens { get; init; }

    /// <summary>Structured-output format parsed once from the agent's response schema, when configured.</summary>
    public ChatResponseFormat? ResponseFormat { get; init; }
}
