using System.Text.Json.Serialization;

namespace Agw.Agents.Definitions.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentSuggestionMode
{
    [JsonStringEnumMemberName("system")]
    System,

    [JsonStringEnumMemberName("claudeCode")]
    ClaudeCode,

    [JsonStringEnumMemberName("unsupported")]
    Unsupported,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentSuggestionKind
{
    [JsonStringEnumMemberName("skill")]
    Skill,

    [JsonStringEnumMemberName("tool")]
    Tool,
}

public sealed record AgentSuggestionResponse(
    string Text,
    string Description,
    AgentSuggestionKind Kind);

public sealed record AgentSuggestionsResponse(
    AgentSuggestionMode Mode,
    IReadOnlyList<AgentSuggestionResponse> Suggestions);
