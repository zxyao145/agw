using System.Text.Json.Nodes;

namespace CodexSdk;

public sealed class TurnOptions
{
    public JsonObject? OutputSchema { get; init; }
    public CancellationToken CancellationToken { get; init; }
}
