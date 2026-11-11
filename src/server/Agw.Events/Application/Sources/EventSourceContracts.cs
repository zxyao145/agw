using System.Text.Json;
using Agw.Shared.Contracts.Events;

namespace Agw.Events.Application.Sources;

public sealed record EventSourceCreateCommand
{
    public required string Name { get; init; }

    public required EventSourceKind Kind { get; init; }

    public required JsonElement Config { get; init; }

    public bool Enabled { get; init; } = true;

    public Guid? ProviderExtensionId { get; init; }

    public string? ProviderId { get; init; }

    public JsonElement? Secret { get; init; }
}

public sealed record EventSourceUpdateCommand
{
    public required Guid Id { get; init; }

    public required long Version { get; init; }

    public required string Name { get; init; }

    public required JsonElement Config { get; init; }

    public JsonElement? Secret { get; init; }
}

public sealed record EventSourceEnableCommand
{
    public required Guid Id { get; init; }

    public required long Version { get; init; }

    public required bool Enabled { get; init; }
}

public sealed record EventSourceVersionCommand
{
    public required Guid Id { get; init; }

    public required long Version { get; init; }
}

public sealed record EventSourceMutationResult
{
    public required Guid Id { get; init; }

    public required long Version { get; init; }

    public string? Secret { get; init; }
}
