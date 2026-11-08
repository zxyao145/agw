using System.Text.Json.Serialization;

namespace Agw.Extensions.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<ExtensionManagementState>))]
public enum ExtensionManagementState
{
    Disabled,
    Starting,
    Loading,
    Prepared,
    Ready,
    Active,
    Draining,
    Stopping,
    Unloading,
    Faulted,
    UnloadFailed,
    Failed,
}

public sealed record ExtensionManagementStatus
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public ExtensionTarget Targets { get; init; }
    public string? CurrentVersion { get; init; }
    public bool IsEnabled { get; init; }
    public long SettingsVersion { get; init; }
    public required ExtensionManagementState State { get; init; }
    public int? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public int ActiveInvocations { get; init; }
    public int ActiveLeases { get; init; }
}

public sealed record ExtensionEnablementRequest(Guid ExtensionId);
