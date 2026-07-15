using Agw.Shared.Exceptions;

using Microsoft.Extensions.AI;

namespace Agw.Integrations.Application.Capabilities;

public sealed class ConnectionCapabilityResolution : IAsyncDisposable
{
    private readonly ConnectionCapabilityLease _lease;

    internal ConnectionCapabilityResolution(
        IReadOnlyList<AITool> nativeTools,
        IReadOnlyList<AITool> mcpTools,
        IReadOnlyList<ResolvedMcpCapabilitySource> mcpSources,
        IReadOnlyList<PluginSkillReference> pluginSkills,
        IReadOnlyList<ConnectionCapabilityWarning> warnings,
        ConnectionCapabilityLease lease)
    {
        NativeTools = nativeTools;
        McpTools = mcpTools;
        Tools = nativeTools.Concat(mcpTools).ToArray();
        McpSources = mcpSources;
        PluginSkills = pluginSkills;
        Warnings = warnings;
        _lease = lease;
    }

    public IReadOnlyList<AITool> NativeTools { get; }

    public IReadOnlyList<AITool> McpTools { get; }

    public IReadOnlyList<AITool> Tools { get; }

    public IReadOnlyList<ResolvedMcpCapabilitySource> McpSources { get; }

    public IReadOnlyList<PluginSkillReference> PluginSkills { get; }

    public IReadOnlyList<ConnectionCapabilityWarning> Warnings { get; }

    public ConnectionCapabilityLease OwnedResourceLease => _lease;

    public ValueTask DisposeAsync()
    {
        return _lease.DisposeAsync();
    }
}

public sealed class ConnectionCapabilityWarning
{
    public required string Code { get; init; }

    public required Guid ConnectionId { get; init; }

    public required string Message { get; init; }
}

public static class ConnectionCapabilityWarningCodes
{
    public const string ConnectionNotFound = "connection_not_found";
    public const string ConnectionDisabled = "connection_disabled";
    public const string ConnectionNeedsConfiguration = "connection_needs_configuration";
    public const string ConnectionPendingAuthorization = "connection_pending_authorization";
    public const string ConnectionUnverified = "connection_unverified";
    public const string ConnectionExpired = "connection_expired";
    public const string ConnectionInvalid = "connection_invalid";
    public const string DefinitionUnavailable = "connection_definition_unavailable";
    public const string PluginInstallationUnavailable = "plugin_installation_unavailable";
    public const string CredentialUnavailable = "connection_credential_unavailable";
    public const string PluginSkillUnavailable = "plugin_skill_unavailable";
}

public sealed class ResolvedMcpCapabilitySource
{
    public required Guid ConnectionId { get; init; }

    public required string SourceId { get; init; }

    public required string Transport { get; init; }

    public required IReadOnlyList<string> ToolNames { get; init; }
}

public sealed class PluginSkillReference
{
    public required string PluginId { get; init; }

    public required string SkillId { get; init; }

    public required string Description { get; init; }

    public required string SkillFilePath { get; init; }
}

public sealed class ConnectionCapabilityLease : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> _resources = [];
    private int _disposed;

    internal void Add(IAsyncDisposable resource)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _resources.Add(resource);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var failed = false;
        for (var index = _resources.Count - 1; index >= 0; index--)
        {
            try
            {
                await _resources[index].DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                failed = true;
            }
        }

        if (failed)
        {
            throw new AgwException(ErrorCodes.IntegrationResourceDisposalFailed);
        }
    }

    internal async ValueTask DisposeWithoutThrowingAsync()
    {
        try
        {
            await DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
