using System.Text.Json.Nodes;

namespace CodexSdk;

public sealed class CodexOptions
{
    public string? CodexPathOverride { get; init; }
    public string? BaseUrl { get; init; }
    public string? ApiKey { get; init; }
    public Dictionary<string, JsonNode?>? Config { get; init; }
    public Dictionary<string, string>? Environment { get; init; }
}
