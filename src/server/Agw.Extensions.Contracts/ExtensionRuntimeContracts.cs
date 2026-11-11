using System.Text.Json;
using Agw.Tools.Abstractions;

namespace Agw.Extensions.Contracts;

public readonly record struct ExtensionGenerationId(Guid ExtensionId, Guid GenerationId);

public sealed record ExtensionExecutionPin(Guid ExtensionId, string ReleaseHash, long Epoch);

public sealed record ExtensionRuntimeLoadRequest
{
    public required Guid ExtensionId { get; init; }

    public required string Name { get; init; }

    public required string Version { get; init; }

    public required string ReleaseHash { get; init; }

    public required long Epoch { get; init; }

    public required string DataDirectory { get; init; }

    public required string PackageDirectory { get; init; }

    public required string EntryAssemblyPath { get; init; }

    public required string EntryType { get; init; }

    public ExtensionEventPermissions EventPermissions { get; init; } = new();
}

public sealed record ExtensionEventPermissions
{
    public IReadOnlyList<string> Publish { get; init; } = [];

    public bool PublishRoot { get; init; }
}

public enum ExtensionGenerationState
{
    Loading,
    Prepared,
    Ready,
    Active,
    Draining,
    Stopping,
    Unloading,
    Faulted,
    UnloadFailed,
}

public sealed record ExtensionGenerationSnapshot
{
    public required ExtensionGenerationId GenerationId { get; init; }

    public required string Name { get; init; }

    public required string Version { get; init; }

    public required string ReleaseHash { get; init; }

    public required long Epoch { get; init; }

    public required ExtensionGenerationState State { get; init; }

    public required IReadOnlyList<ExtensionEndpointDescriptor> Endpoints { get; init; }

    public required IReadOnlyList<ExtensionToolDescriptor> Tools { get; init; }

    public required IReadOnlyList<ExtensionSkillDescriptor> Skills { get; init; }

    public IReadOnlyList<ExtensionEventHandlerDescriptor> EventHandlers { get; init; } = [];

    public IReadOnlyList<ExtensionEventSourceProviderDescriptor> EventSourceProviders { get; init; } = [];

    public IReadOnlyList<ExtensionNotificationChannelDescriptor> NotificationChannels { get; init; } = [];

    public ExtensionEventPermissions EventPermissions { get; init; } = new();

    public int ActiveInvocations { get; init; }

    public int ActiveLeases { get; init; }
}

public sealed record ExtensionEndpointDescriptor
{
    public required string Id { get; init; }

    public required string Method { get; init; }

    public required string RelativePath { get; init; }

    public string? Summary { get; init; }

    public bool IsCallback { get; init; }

    public bool IsBrowserPage { get; init; }

    public JsonElement? RequestSchema { get; init; }

    public JsonElement? ResponseSchema { get; init; }
}

public sealed record ExtensionEndpointRequest
{
    public required string Method { get; init; }

    public required IReadOnlyDictionary<string, IReadOnlyList<string>> Query { get; init; }

    public JsonElement? Body { get; init; }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Headers { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    public byte[]? RawBody { get; init; }
}

public sealed record ExtensionToolDescriptor
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required AgwToolPermission RequiredPermission { get; init; }

    public required JsonElement InputSchema { get; init; }

    public required string Category { get; init; }

    public required bool AllowInPlanMode { get; init; }
}

public sealed record ExtensionSkillDescriptor(string Id, string Name, string Description);

public sealed record ExtensionEndpointInvocation
{
    public required string ReleaseHash { get; init; }

    public required long Epoch { get; init; }

    public required string EndpointId { get; init; }

    public required JsonElement Request { get; init; }
}

public sealed record ExtensionToolInvocation
{
    public required string ReleaseHash { get; init; }

    public required long Epoch { get; init; }

    public required string ToolId { get; init; }

    public required JsonElement Arguments { get; init; }
}

public sealed record ExtensionSkillInvocation
{
    public required string ReleaseHash { get; init; }

    public required long Epoch { get; init; }

    public required string SkillId { get; init; }
}

public interface IExtensionInvocationResponse : IAsyncDisposable
{
    int StatusCode { get; }

    string ContentType { get; }

    JsonElement? JsonBody { get; }

    IAsyncEnumerable<ReadOnlyMemory<byte>>? BodyStream { get; }

    ValueTask CompleteAsync(CancellationToken cancellationToken = default);
}

public interface IExtensionGenerationLease : IAsyncDisposable
{
    ExtensionGenerationSnapshot Snapshot { get; }

    ExtensionExecutionPin Pin { get; }
}

public interface IExtensionRuntimeAvailability
{
    ValueTask<bool> CanAcceptNewWorkAsync(ExtensionExecutionPin pin, CancellationToken cancellationToken = default);

    ValueTask<bool> CanContinueExecutionAsync(Guid extensionId, CancellationToken cancellationToken = default);

    ValueTask<bool> CanContinuePinnedWorkAsync(
        ExtensionExecutionPin pin,
        CancellationToken cancellationToken = default
    );
}

public interface IExtensionGenerationCatalog
{
    IReadOnlyList<ExtensionGenerationSnapshot> GetSnapshots();

    ValueTask<IExtensionGenerationLease> AcquireCurrentAsync(
        Guid extensionId,
        CancellationToken cancellationToken = default
    );

    ValueTask<IExtensionGenerationLease> AcquireForExecutionAsync(
        Guid extensionId,
        CancellationToken cancellationToken = default
    );
}

public interface IExtensionRuntime : IExtensionGenerationCatalog, IAsyncDisposable
{
    ValueTask StopAsync(CancellationToken cancellationToken = default);

    ValueTask<ExtensionGenerationSnapshot> LoadAsync(
        ExtensionRuntimeLoadRequest request,
        CancellationToken cancellationToken = default
    );

    ValueTask PrepareDatabaseAsync(ExtensionGenerationId generationId, CancellationToken cancellationToken = default);

    ValueTask ActivateAsync(ExtensionGenerationId generationId, CancellationToken cancellationToken = default);

    ValueTask<bool> DrainAsync(
        ExtensionGenerationId generationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default
    );

    ValueTask ResumeAsync(ExtensionGenerationId generationId, CancellationToken cancellationToken = default);

    ValueTask UnloadAsync(
        ExtensionGenerationId generationId,
        TimeSpan unloadConfirmationTimeout,
        CancellationToken cancellationToken = default
    );
}

public interface IExtensionInvoker
{
    ValueTask<IExtensionInvocationResponse> InvokeEndpointAsync(
        IExtensionGenerationLease generationLease,
        ExtensionEndpointInvocation invocation,
        CancellationToken cancellationToken = default
    );

    ValueTask<IExtensionInvocationResponse> InvokeToolAsync(
        IExtensionGenerationLease generationLease,
        ExtensionToolInvocation invocation,
        CancellationToken cancellationToken = default
    );

    ValueTask<IExtensionInvocationResponse> InvokeSkillContentAsync(
        IExtensionGenerationLease generationLease,
        ExtensionSkillInvocation invocation,
        CancellationToken cancellationToken = default
    );

    ValueTask<ExtensionEventHandlerResult> InvokeEventHandlerAsync(
        IExtensionGenerationLease generationLease,
        ExtensionEventHandlerInvocation invocation,
        CancellationToken cancellationToken = default
    );

    ValueTask<ExtensionSourcePollResult> InvokeSourcePollAsync(
        IExtensionGenerationLease generationLease,
        ExtensionSourcePollInvocation invocation,
        CancellationToken cancellationToken = default
    );

    ValueTask<ExtensionNotificationChannelResult> InvokeNotificationChannelAsync(
        IExtensionGenerationLease generationLease,
        ExtensionNotificationChannelInvocation invocation,
        CancellationToken cancellationToken = default
    );
}
