using System.Text.Json;
using Agw.Shared.Exceptions;
using Json.Path;
using Markdig;
using Markdig.Syntax;

namespace Agw.Extensions.TaskPilot;

public sealed record TaskPilotDeliveryRuleConfig
{
    public required Guid ConnectionId { get; init; }
    public required string Repository { get; init; }
    public int CheckIntervalSeconds { get; init; } = 60;

    public static TaskPilotDeliveryRuleConfig Parse(string json)
    {
        var config =
            JsonSerializer.Deserialize(json, TaskPilotDeliveryJsonContext.Default.TaskPilotDeliveryRuleConfig)
            ?? throw new AgwException(ErrorCodes.InvalidParam);
        if (
            config.ConnectionId == Guid.Empty
            || string.IsNullOrWhiteSpace(config.Repository)
            || config.Repository.Split('/') is not { Length: 2 } parts
            || parts.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or "..")
            || config.CheckIntervalSeconds is < 10 or > 86400
        )
            throw new AgwException(ErrorCodes.InvalidParam, "The delivery rule configuration is invalid.");
        return config;
    }
}

public sealed record TaskPilotDeliveryProjectConfig
{
    public required string Repo { get; init; }
    public required string Branch { get; init; }
    public required TaskPilotDeliveryReviewConfig Review { get; init; }
    public required TaskPilotDeliveryCheckConfig Check { get; init; }
    public required TaskPilotDeliveryWorkflowConfig Ship { get; init; }
    public required TaskPilotDeliveryHealthConfig Health { get; init; }
    public IReadOnlyList<string> Internal { get; init; } = [];

    public static TaskPilotDeliveryProjectConfig Parse(string json, string repository)
    {
        var config =
            JsonSerializer.Deserialize(json, TaskPilotDeliveryJsonContext.Default.TaskPilotDeliveryProjectConfig)
            ?? throw new AgwException(ErrorCodes.InvalidParam);
        if (
            config.Review is null
            || config.Check is null
            || config.Ship is null
            || config.Health is null
            || !string.Equals(config.Repo, repository, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(config.Branch)
            || string.IsNullOrWhiteSpace(config.Review.Status)
            || string.IsNullOrWhiteSpace(config.Check.Workflow)
            || string.IsNullOrWhiteSpace(config.Check.Job)
            || string.IsNullOrWhiteSpace(config.Ship.Workflow)
            || !Uri.TryCreate(config.Health.Url, UriKind.Absolute, out var healthUri)
            || healthUri.Scheme != "https"
        )
            throw new AgwException(ErrorCodes.InvalidParam, "The delivery project configuration is invalid.");
        _ = JsonPath.Parse(config.Health.Version);
        return config;
    }
}

public sealed record TaskPilotDeliveryReviewConfig(string Status);

public sealed record TaskPilotDeliveryCheckConfig(string Workflow, string Job);

public sealed record TaskPilotDeliveryWorkflowConfig(string Workflow);

public sealed record TaskPilotDeliveryHealthConfig(string Url, string Version);

public sealed record TaskPilotReviewResult
{
    public required bool Approved { get; init; }
    public required string Summary { get; init; }
    public required IReadOnlyList<TaskPilotReviewFinding> Findings { get; init; }
}

public sealed record TaskPilotReviewFinding
{
    public required string File { get; init; }
    public int? Line { get; init; }
    public required string Message { get; init; }
    public required string Severity { get; init; }
}

public static class TaskPilotReviewParser
{
    public static TaskPilotReviewResult Parse(string text)
    {
        var blocks = Markdown
            .Parse(text)
            .Descendants<FencedCodeBlock>()
            .Where(block => string.Equals(block.Info, "taskpilot-review", StringComparison.Ordinal))
            .ToArray();
        if (blocks.Length != 1)
            throw new AgwException(ErrorCodes.InvalidParam, "A review requires one taskpilot-review code block.");
        var result =
            JsonSerializer.Deserialize(
                blocks[0].Lines.ToString(),
                TaskPilotDeliveryJsonContext.Default.TaskPilotReviewResult
            ) ?? throw new AgwException(ErrorCodes.InvalidParam);
        if (
            string.IsNullOrWhiteSpace(result.Summary)
            || result.Findings is null
            || result.Approved && result.Findings.Count != 0
            || result.Findings.Any(finding =>
                finding is null
                || string.IsNullOrWhiteSpace(finding.File)
                || string.IsNullOrWhiteSpace(finding.Message)
                || finding.Severity is not ("error" or "warning")
                || finding.Line <= 0
            )
        )
            throw new AgwException(ErrorCodes.InvalidParam, "The review result is invalid.");
        return result;
    }
}
