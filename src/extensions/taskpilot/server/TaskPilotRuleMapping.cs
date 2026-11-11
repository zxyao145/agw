using System.Text.Json;

namespace Agw.Extensions.TaskPilot;

internal static class TaskPilotRuleMapping
{
    public static TaskPilotRuleDetails ToDetails(TaskPilotRule rule, JsonSerializerOptions jsonOptions)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(jsonOptions);

        return new TaskPilotRuleDetails
        {
            Rule = ToSummary(rule),
            SourceIds = JsonSerializer.Deserialize<Guid[]>(rule.SourceIdsJson, jsonOptions) ?? [],
            EventTypes = JsonSerializer.Deserialize<string[]>(rule.EventTypesJson, jsonOptions) ?? [],
            Condition = ParseOptionalJson(rule.ConditionJson),
            MatterKey = ParseOptionalJson(rule.MatterKeyJson),
            TargetKind = rule.TargetKind,
            TargetId = rule.TargetId,
            ProjectId = rule.ProjectId,
            Instructions = rule.Instructions,
            Skill = rule.Skill,
            DataScope = JsonSerializer.Deserialize<TaskPilotDataScope>(rule.DataScopeJson, jsonOptions) ?? new(),
            PermissionMode = rule.PermissionMode,
            AskFirst = JsonSerializer.Deserialize<string[]>(rule.AskFirstJson, jsonOptions) ?? [],
            GroupKey = rule.GroupKey,
            MaxConcurrency = rule.MaxConcurrency,
            SessionPolicy = rule.SessionPolicy,
            Channels = JsonSerializer.Deserialize<string[]>(rule.ChannelsJson, jsonOptions) ?? [],
            NotifyPolicy = rule.NotifyPolicy,
            EmitTypes = JsonSerializer.Deserialize<string[]>(rule.EmitTypesJson, jsonOptions) ?? [],
            Retry = JsonSerializer.Deserialize<TaskPilotRetryPolicy>(rule.RetryJson, jsonOptions) ?? new(),
            Delivery = ParseOptionalJson(rule.DeliveryJson),
        };
    }

    public static TaskPilotRuleSummary ToSummary(TaskPilotRule rule) =>
        new()
        {
            Id = rule.Id,
            Name = rule.Name,
            Kind = rule.Kind,
            Enabled = rule.Enabled,
            Paused = rule.Paused,
            DeletedAt = rule.DeletedAt,
            Version = rule.Version,
            SubscriptionId = rule.SubscriptionId,
            SubscriptionPending = rule.SubscriptionId is null && rule.SubscriptionOutboxId is not null,
            SubscriptionError = rule.SubscriptionError,
        };

    public static string Serialize(TaskPilotRuleDetails details, JsonSerializerOptions jsonOptions) =>
        JsonSerializer.Serialize(details, jsonOptions);

    public static JsonElement? ParseOptionalJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || string.Equals(json, "null", StringComparison.Ordinal))
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static string SerializeOptionalJson(JsonElement? element) =>
        element is { } value ? value.GetRawText() : "null";
}
