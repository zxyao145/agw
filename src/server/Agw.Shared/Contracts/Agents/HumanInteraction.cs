using System.Text.Json;

namespace Agw.Shared.Contracts.Agents;

public sealed record HumanInteractionRequest(
    string RequestId,
    string InteractionKind,
    string Prompt,
    JsonElement Payload)
{
    public string? ToolName { get; init; }

    public string? CallId { get; init; }
}

public sealed record HumanInteractionResponse(
    string RequestId,
    bool Cancelled,
    JsonElement? ResponseData);

public interface IHumanInteractionChannel
{
    ValueTask<HumanInteractionResponse> RequestAsync(
        HumanInteractionRequest request,
        CancellationToken cancellationToken);
}

public interface IHumanInteractionContextAccessor
{
    IHumanInteractionChannel? Current { get; }
}
