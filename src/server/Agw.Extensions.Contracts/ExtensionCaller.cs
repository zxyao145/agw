namespace Agw.Extensions.Contracts;

public sealed record ExtensionCaller
{
    public required Guid ExtensionId { get; init; }

    public required Guid GenerationId { get; init; }

    public string? UserId { get; init; }

    public required Guid InvocationId { get; init; }
}

public interface IExtensionCallerAccessor
{
    ExtensionCaller? Current { get; }
}
