using System.Text.Json;
using Agw.Shared.Contracts.Events;

namespace Agw.Events.Api.Dtos;

public sealed record EventSourceCreateRequest
{
    public required string Name { get; init; }

    public required EventSourceKind Kind { get; init; }

    public required JsonElement Config { get; init; }

    public bool Enabled { get; init; } = true;

    public Guid? ProviderExtensionId { get; init; }

    public string? ProviderId { get; init; }

    public JsonElement? Secret { get; init; }
}

public sealed record EventSourceUpdateRequest
{
    public required Guid Id { get; init; }

    public required long Version { get; init; }

    public required string Name { get; init; }

    public required JsonElement Config { get; init; }

    public JsonElement? Secret { get; init; }
}

public sealed record EventSourceEnableRequest
{
    public required Guid Id { get; init; }

    public required long Version { get; init; }

    public required bool Enabled { get; init; }
}

public sealed record EventSourceVersionRequest
{
    public required Guid Id { get; init; }

    public required long Version { get; init; }
}

public sealed record EventSourceIdRequest
{
    public required Guid Id { get; init; }
}

public sealed record EventSourceResponse
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required EventSourceKind Kind { get; init; }

    public Guid? ProviderExtensionId { get; init; }

    public string? ProviderId { get; init; }

    public required string ManagedBy { get; init; }

    public string? ManagedKey { get; init; }

    public required string CeSource { get; init; }

    public required JsonElement Config { get; init; }

    public required bool Enabled { get; init; }

    public DateTimeOffset? BaselineAt { get; init; }

    public DateTimeOffset? NextRunAt { get; init; }

    public DateTimeOffset? LastRunAt { get; init; }

    public string? LastError { get; init; }

    public required bool HasSecret { get; init; }

    public required long Version { get; init; }
}

public sealed record EventSourceMutationResponse
{
    public required Guid Id { get; init; }

    public required long Version { get; init; }

    public string? Secret { get; init; }
}

public sealed record EventSourceTestResponse
{
    public required int ItemCount { get; init; }
}

public sealed record EventWebhookReceiveResponse
{
    public required Guid EventId { get; init; }
}
