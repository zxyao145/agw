namespace Agw.Shared.Data.Entities.Projects;

/// <summary>A value object representing an additional directory associated with a Project.</summary>
public sealed record ProjectDirectory
{
    public Guid Id { get; init; }

    public required string Path { get; init; }
}
