using Agw.Auth.Application;
using Agw.Integrations.Application.Credentials;
using Agw.Integrations.Application.Management;
using Agw.Integrations.Application.Plugins;
using Agw.Integrations.Domain.Plugins;
using Agw.Integrations.Mcp;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using IntegrationConnection = Agw.Shared.Data.Entities.Integrations.Connection;

namespace Agw.Integrations.Application.Capabilities;

public sealed class ConnectionCapabilityResolver : IConnectionCapabilityResolver, IConnectionMcpInvocationSession
{
    private readonly IRepository<IntegrationConnection> _connectionRepository;
    private readonly IRepository<PluginInstallation> _installationRepository;
    private readonly IPluginCatalog _pluginCatalog;
    private readonly IConnectionCredentialReader _credentialReader;
    private readonly IReadOnlyDictionary<string, IConnectionNativeCapabilityProvider> _nativeProviders;
    private readonly IMcpToolMaterializer _mcpToolMaterializer;
    private readonly IConnectionMcpToolInvoker _mcpToolInvoker;
    private readonly PluginSkillMetadataReader _pluginSkillMetadataReader;
    private readonly TimeProvider _timeProvider;
    private readonly IUserInfoService _userInfoService;

    public ConnectionCapabilityResolver(
        IRepository<IntegrationConnection> connectionRepository,
        IRepository<PluginInstallation> installationRepository,
        IPluginCatalog pluginCatalog,
        IConnectionCredentialReader credentialReader,
        IEnumerable<IConnectionNativeCapabilityProvider> nativeProviders,
        IMcpToolMaterializer mcpToolMaterializer,
        IConnectionMcpToolInvoker mcpToolInvoker,
        PluginSkillMetadataReader pluginSkillMetadataReader,
        TimeProvider timeProvider,
        IUserInfoService userInfoService
    )
    {
        _connectionRepository = connectionRepository;
        _installationRepository = installationRepository;
        _pluginCatalog = pluginCatalog;
        _credentialReader = credentialReader;
        _nativeProviders = nativeProviders.ToDictionary(item => item.Provider, StringComparer.OrdinalIgnoreCase);
        _mcpToolMaterializer = mcpToolMaterializer;
        _mcpToolInvoker = mcpToolInvoker;
        _pluginSkillMetadataReader = pluginSkillMetadataReader;
        _timeProvider = timeProvider;
        _userInfoService = userInfoService;
    }

    public async Task<ConnectionCapabilityResolution> ResolveAsync(
        Guid projectId,
        IReadOnlyCollection<Guid> connectionIds,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(connectionIds);
        var userId = _userInfoService.RequiredUserId;

        var orderedIds = connectionIds.Distinct().ToArray();
        var storedConnections = await _connectionRepository
            .Queryable.AsNoTracking()
            .Where(item => orderedIds.Contains(item.Id) && item.CreateBy == userId)
            .ToListAsync(cancellationToken);
        var connections = storedConnections.ToDictionary(item => item.Id);
        var pluginIds = storedConnections.Select(item => item.PluginId).Distinct().ToArray();
        var installations = await _installationRepository
            .Queryable.AsNoTracking()
            .Where(item => pluginIds.Contains(item.PluginId))
            .ToDictionaryAsync(item => item.PluginId, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var nativeTools = new List<AITool>();
        var mcpTools = new List<AITool>();
        var mcpSources = new List<ResolvedMcpCapabilitySource>();
        var pluginSkills = new List<PluginSkillReference>();
        var warnings = new List<ConnectionCapabilityWarning>();
        var resolvedSkillPlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lease = new ConnectionCapabilityLease();

        try
        {
            foreach (var connectionId in orderedIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!connections.TryGetValue(connectionId, out var connection))
                {
                    warnings.Add(
                        Warning(
                            connectionId,
                            ConnectionCapabilityWarningCodes.ConnectionNotFound,
                            "The integration was not found."
                        )
                    );
                    continue;
                }

                var resolved = await ResolveReadyConnectionAsync(
                    connection,
                    installations,
                    warnings,
                    cancellationToken
                );
                if (resolved == null)
                {
                    continue;
                }

                foreach (var source in resolved.Connector.CapabilitySources)
                {
                    if (source is NativeCapabilitySourceDefinition nativeSource)
                    {
                        AddNativeTools(projectId, connection, nativeSource, nativeTools, toolNames);
                        continue;
                    }

                    if (source is McpCapabilitySourceDefinition mcpSource)
                    {
                        await AddMcpToolsAsync(
                            connection,
                            resolved.Installation,
                            mcpSource,
                            mcpTools,
                            mcpSources,
                            toolNames,
                            cancellationToken
                        );
                    }
                }

                if (resolvedSkillPlugins.Add(resolved.Plugin.Id))
                {
                    AddPluginSkills(connection.Id, resolved.Plugin, pluginSkills, warnings);
                }
            }

            return new ConnectionCapabilityResolution(nativeTools, mcpTools, mcpSources, pluginSkills, warnings, lease);
        }
        catch (OperationCanceledException)
        {
            await lease.DisposeWithoutThrowingAsync().ConfigureAwait(false);
            throw;
        }
        catch (AgwException)
        {
            await lease.DisposeWithoutThrowingAsync().ConfigureAwait(false);
            throw;
        }
        catch
        {
            await lease.DisposeWithoutThrowingAsync().ConfigureAwait(false);
            throw new AgwException(ErrorCodes.IntegrationCapabilityResolutionFailed);
        }
    }

    private async Task<ReadyConnection?> ResolveReadyConnectionAsync(
        IntegrationConnection connection,
        IReadOnlyDictionary<string, PluginInstallation> installations,
        ICollection<ConnectionCapabilityWarning> warnings,
        CancellationToken cancellationToken
    )
    {
        if (!connection.Enabled)
        {
            warnings.Add(
                Warning(
                    connection.Id,
                    ConnectionCapabilityWarningCodes.ConnectionDisabled,
                    "The integration is disabled."
                )
            );
            return null;
        }

        var plugin = _pluginCatalog.Find(connection.PluginId);
        var connector = plugin?.Connectors.FirstOrDefault(item =>
            string.Equals(item.Id, connection.ConnectorId, StringComparison.OrdinalIgnoreCase)
        );
        var authScheme = connector?.AuthSchemes.FirstOrDefault(item =>
            string.Equals(item.Id, connection.AuthSchemeId, StringComparison.OrdinalIgnoreCase)
        );
        if (plugin == null || connector == null || authScheme == null)
        {
            warnings.Add(
                Warning(
                    connection.Id,
                    ConnectionCapabilityWarningCodes.DefinitionUnavailable,
                    "The integration definition is unavailable."
                )
            );
            return null;
        }

        if (!installations.TryGetValue(connection.PluginId, out var installation) || !installation.Enabled)
        {
            warnings.Add(
                Warning(
                    connection.Id,
                    ConnectionCapabilityWarningCodes.PluginInstallationUnavailable,
                    "The integration setup is unavailable."
                )
            );
            return null;
        }

        if (connection.Status != ConnectionStatus.Ready)
        {
            warnings.Add(StatusWarning(connection));
            return null;
        }

        var credentialStatus = await CheckRequiredCredentialsAsync(
            connection,
            installation,
            connector,
            authScheme,
            cancellationToken
        );
        if (credentialStatus != null)
        {
            warnings.Add(credentialStatus);
            return null;
        }

        return new ReadyConnection(plugin, connector, authScheme, installation);
    }

    private async Task<ConnectionCapabilityWarning?> CheckRequiredCredentialsAsync(
        IntegrationConnection connection,
        PluginInstallation installation,
        ConnectorDefinition connector,
        AuthSchemeDefinition authScheme,
        CancellationToken cancellationToken
    )
    {
        Dictionary<string, string?> connectionConfiguration;
        Dictionary<string, string?> installationConfiguration;
        try
        {
            connectionConfiguration = IntegrationConfigurationCodec.Read(connection.ConfigurationJson);
            installationConfiguration = IntegrationConfigurationCodec.Read(installation.ConfigurationJson);
        }
        catch
        {
            return Warning(
                connection.Id,
                ConnectionCapabilityWarningCodes.ConnectionNeedsConfiguration,
                "The integration configuration is unavailable."
            );
        }

        foreach (
            var field in authScheme.InstallationFields.Where(item =>
                item.IsRequired && item.Type != FormFieldType.Secret
            )
        )
        {
            var key = IntegrationConfigurationCodec.InstallationKey(
                connection.ConnectorId,
                connection.AuthSchemeId,
                field.Id
            );
            if (!installationConfiguration.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                return Warning(
                    connection.Id,
                    ConnectionCapabilityWarningCodes.ConnectionNeedsConfiguration,
                    "A required integration setup value is unavailable."
                );
            }
        }

        foreach (
            var field in authScheme.ConnectionFields.Where(item => item.IsRequired && item.Type != FormFieldType.Secret)
        )
        {
            if (!connectionConfiguration.TryGetValue(field.Id, out var value) || string.IsNullOrWhiteSpace(value))
            {
                return Warning(
                    connection.Id,
                    ConnectionCapabilityWarningCodes.ConnectionNeedsConfiguration,
                    "A required integration setting is unavailable."
                );
            }
        }

        if (authScheme.Type == AuthSchemeType.OAuth2)
        {
            var accessToken = await TryReadConnectionCredentialAsync(
                connection.Id,
                IntegrationCredentialSlots.OAuthAccessToken,
                cancellationToken
            );
            if (accessToken == null)
            {
                return Warning(
                    connection.Id,
                    ConnectionCapabilityWarningCodes.CredentialUnavailable,
                    "A required integration credential is unavailable."
                );
            }

            if (accessToken.ExpiresAtUtc <= _timeProvider.GetUtcNow())
            {
                return Warning(
                    connection.Id,
                    ConnectionCapabilityWarningCodes.ConnectionExpired,
                    "The integration credential is expired."
                );
            }
        }

        foreach (
            var field in authScheme.InstallationFields.Where(item =>
                item.IsRequired && item.Type == FormFieldType.Secret
            )
        )
        {
            var credential = await TryReadInstallationCredentialAsync(
                installation.Id,
                IntegrationCredentialSlots.InstallationField(connection.ConnectorId, connection.AuthSchemeId, field.Id),
                cancellationToken
            );
            if (credential == null)
            {
                return Warning(
                    connection.Id,
                    ConnectionCapabilityWarningCodes.CredentialUnavailable,
                    "A required integration setup credential is unavailable."
                );
            }
        }

        foreach (
            var field in authScheme.ConnectionFields.Where(item => item.IsRequired && item.Type == FormFieldType.Secret)
        )
        {
            var credential = await TryReadConnectionCredentialAsync(
                connection.Id,
                IntegrationCredentialSlots.ConnectionField(field.Id),
                cancellationToken
            );
            if (credential == null)
            {
                return Warning(
                    connection.Id,
                    ConnectionCapabilityWarningCodes.CredentialUnavailable,
                    "A required integration credential is unavailable."
                );
            }

            if (credential.ExpiresAtUtc <= _timeProvider.GetUtcNow())
            {
                return Warning(
                    connection.Id,
                    ConnectionCapabilityWarningCodes.ConnectionExpired,
                    "A required integration credential is expired."
                );
            }
        }

        foreach (var source in connector.CapabilitySources.OfType<McpCapabilitySourceDefinition>())
        {
            foreach (
                var binding in source.CredentialBindings.Where(item =>
                    string.Equals(
                        item.ValueSource.AuthSchemeId,
                        connection.AuthSchemeId,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
            )
            {
                var credential = await TryReadBindingCredentialAsync(
                    connection,
                    installation,
                    binding,
                    cancellationToken
                );
                if (credential == null)
                {
                    return Warning(
                        connection.Id,
                        ConnectionCapabilityWarningCodes.CredentialUnavailable,
                        "A declared capability credential is unavailable."
                    );
                }

                if (credential.ExpiresAtUtc <= _timeProvider.GetUtcNow())
                {
                    return Warning(
                        connection.Id,
                        ConnectionCapabilityWarningCodes.ConnectionExpired,
                        "A declared capability credential is expired."
                    );
                }
            }
        }

        return null;
    }

    private void AddNativeTools(
        Guid projectId,
        IntegrationConnection connection,
        NativeCapabilitySourceDefinition source,
        ICollection<AITool> target,
        ISet<string> toolNames
    )
    {
        if (!_nativeProviders.TryGetValue(source.Provider, out var provider))
        {
            throw new AgwException(
                ErrorCodes.IntegrationNativeProviderUnavailable,
                $"Native capability provider '{source.Provider}' is unavailable."
            );
        }

        var tools = provider.CreateTools(
            new ConnectionNativeCapabilityContext
            {
                ConnectionId = connection.Id,
                ProjectId = projectId,
                Alias = connection.Alias,
                Source = source,
            }
        );
        var requiredPrefix = $"{connection.Alias}__";
        foreach (var tool in tools)
        {
            if (
                !tool.Name.StartsWith(requiredPrefix, StringComparison.Ordinal)
                || tool.Name.Length == requiredPrefix.Length
            )
            {
                throw new AgwException(ErrorCodes.IntegrationToolNameInvalid);
            }

            AddTool(tool, target, toolNames);
        }
    }

    private async Task AddMcpToolsAsync(
        IntegrationConnection connection,
        PluginInstallation installation,
        McpCapabilitySourceDefinition source,
        ICollection<AITool> target,
        ICollection<ResolvedMcpCapabilitySource> sources,
        ISet<string> toolNames,
        CancellationToken cancellationToken
    )
    {
        var descriptor = await CreateMcpDescriptorAsync(connection, installation, source, cancellationToken);
        await using var sourceLease = await _mcpToolMaterializer.MaterializeAsync(
            descriptor,
            runtimeOverrides: null,
            cancellationToken
        );

        var names = new List<string>();
        foreach (var rawTool in sourceLease.Tools)
        {
            if (rawTool is not AIFunction function || string.IsNullOrWhiteSpace(rawTool.Name))
            {
                throw new AgwException(ErrorCodes.IntegrationMcpMaterializationFailed);
            }

            var tool = new RefreshingMcpAIFunction(
                $"{connection.Alias}__{function.Name}",
                connection.Id,
                source.Id,
                function,
                _mcpToolInvoker
            );
            AddTool(tool, target, toolNames);
            names.Add(tool.Name);
        }

        sources.Add(
            new ResolvedMcpCapabilitySource
            {
                ConnectionId = connection.Id,
                SourceId = source.Id,
                Transport = source.Transport switch
                {
                    StdioMcpTransportDefinition => "stdio",
                    HttpMcpTransportDefinition => "http",
                    SseMcpTransportDefinition => "sse",
                    _ => "unknown",
                },
                ToolNames = names,
            }
        );
    }

    async ValueTask<object?> IConnectionMcpInvocationSession.InvokeMcpToolAsync(
        Guid connectionId,
        string sourceId,
        string operationName,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken
    )
    {
        return await InvokeMcpToolAsync(connectionId, sourceId, operationName, arguments, cancellationToken);
    }

    internal async ValueTask<object?> InvokeMcpToolAsync(
        Guid connectionId,
        string sourceId,
        string operationName,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var userId = _userInfoService.RequiredUserId;
            var connection = await _connectionRepository
                .Queryable.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == connectionId && item.CreateBy == userId, cancellationToken);
            if (connection == null)
            {
                throw new AgwException(ErrorCodes.IntegrationDataInvalid);
            }

            var installation = await _installationRepository
                .Queryable.AsNoTracking()
                .SingleOrDefaultAsync(item => item.PluginId == connection.PluginId, cancellationToken);
            var plugin = _pluginCatalog.Find(connection.PluginId);
            var connector = plugin?.Connectors.FirstOrDefault(item =>
                string.Equals(item.Id, connection.ConnectorId, StringComparison.OrdinalIgnoreCase)
            );
            var authSchemeExists =
                connector?.AuthSchemes.Any(item =>
                    string.Equals(item.Id, connection.AuthSchemeId, StringComparison.OrdinalIgnoreCase)
                ) == true;
            var source = connector
                ?.CapabilitySources.OfType<McpCapabilitySourceDefinition>()
                .FirstOrDefault(item => string.Equals(item.Id, sourceId, StringComparison.OrdinalIgnoreCase));
            if (installation == null || !authSchemeExists || source == null)
            {
                throw new AgwException(ErrorCodes.IntegrationDataInvalid);
            }

            var descriptor = await CreateMcpDescriptorAsync(connection, installation, source, cancellationToken);
            await using var invocationLease = await _mcpToolMaterializer.MaterializeAsync(
                descriptor,
                runtimeOverrides: null,
                cancellationToken
            );
            var function = invocationLease
                .Tools.OfType<AIFunction>()
                .SingleOrDefault(item => string.Equals(item.Name, operationName, StringComparison.Ordinal));
            if (function == null)
            {
                throw new AgwException(ErrorCodes.IntegrationMcpMaterializationFailed);
            }

            return await function.InvokeAsync(arguments, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            throw new AgwException(ErrorCodes.IntegrationMcpMaterializationFailed);
        }
    }

    private async Task<McpEndpointDescriptor> CreateMcpDescriptorAsync(
        IntegrationConnection connection,
        PluginInstallation installation,
        McpCapabilitySourceDefinition source,
        CancellationToken cancellationToken
    )
    {
        var credentialEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var credentialHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in source.CredentialBindings)
        {
            if (
                !string.Equals(
                    binding.ValueSource.AuthSchemeId,
                    connection.AuthSchemeId,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                continue;
            }

            var value = await ResolveBindingValueAsync(connection, installation, binding, cancellationToken);
            var targetValues =
                binding.Target == CredentialBindingTarget.EnvironmentVariable
                    ? credentialEnvironment
                    : credentialHeaders;
            targetValues[binding.TargetName] = string.Concat(binding.ValuePrefix, value);
        }

        return CreateMcpDescriptor(
            $"{connection.Alias}__{source.Id}",
            source.Transport,
            credentialEnvironment,
            credentialHeaders
        );
    }

    private async Task<string> ResolveBindingValueAsync(
        IntegrationConnection connection,
        PluginInstallation installation,
        CredentialBindingDefinition binding,
        CancellationToken cancellationToken
    )
    {
        var credential = await TryReadBindingCredentialAsync(connection, installation, binding, cancellationToken);

        if (credential == null)
        {
            throw new AgwException(ErrorCodes.IntegrationMcpMaterializationFailed);
        }

        return credential.Value;
    }

    private async Task<ResolvedCredential?> TryReadBindingCredentialAsync(
        IntegrationConnection connection,
        PluginInstallation installation,
        CredentialBindingDefinition binding,
        CancellationToken cancellationToken
    )
    {
        return binding.ValueSource switch
        {
            ConnectionFieldCredentialValueSourceDefinition connectionField => await TryReadConnectionCredentialAsync(
                connection.Id,
                IntegrationCredentialSlots.ConnectionField(connectionField.FieldId),
                cancellationToken
            ),
            InstallationFieldCredentialValueSourceDefinition installationField =>
                await TryReadInstallationCredentialAsync(
                    installation.Id,
                    IntegrationCredentialSlots.InstallationField(
                        connection.ConnectorId,
                        connection.AuthSchemeId,
                        installationField.FieldId
                    ),
                    cancellationToken
                ),
            OAuthAccessTokenCredentialValueSourceDefinition => await TryReadConnectionCredentialAsync(
                connection.Id,
                IntegrationCredentialSlots.OAuthAccessToken,
                cancellationToken
            ),
            _ => null,
        };
    }

    private static McpEndpointDescriptor CreateMcpDescriptor(
        string name,
        McpTransportDefinition transport,
        IReadOnlyDictionary<string, string> credentialEnvironment,
        IReadOnlyDictionary<string, string> credentialHeaders
    )
    {
        return transport switch
        {
            StdioMcpTransportDefinition stdio => new McpStdioEndpointDescriptor(
                name,
                stdio.Command,
                stdio.Arguments,
                credentialEnvironmentVariables: credentialEnvironment
            ),
            HttpMcpTransportDefinition http => new McpHttpEndpointDescriptor(
                name,
                new Uri(http.Endpoint, UriKind.Absolute),
                credentialHeaders: credentialHeaders
            ),
            SseMcpTransportDefinition sse => new McpSseEndpointDescriptor(
                name,
                new Uri(sse.Endpoint, UriKind.Absolute),
                credentialHeaders: credentialHeaders
            ),
            _ => throw new AgwException(ErrorCodes.UnsupportedTransportType),
        };
    }

    private void AddPluginSkills(
        Guid connectionId,
        PluginDefinition plugin,
        ICollection<PluginSkillReference> target,
        ICollection<ConnectionCapabilityWarning> warnings
    )
    {
        foreach (var skill in plugin.Skills)
        {
            if (!_pluginSkillMetadataReader.TryRead(skill, out var metadata))
            {
                warnings.Add(SkillWarning(connectionId));
                continue;
            }

            target.Add(
                new PluginSkillReference
                {
                    PluginId = plugin.Id,
                    SkillId = metadata.Id,
                    Description = metadata.Description,
                    SkillFilePath = metadata.SkillFilePath,
                }
            );
        }
    }

    private async Task<ResolvedCredential?> TryReadConnectionCredentialAsync(
        Guid connectionId,
        string slot,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await _credentialReader.ReadConnectionAsync(connectionId, slot, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<ResolvedCredential?> TryReadInstallationCredentialAsync(
        Guid installationId,
        string slot,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await _credentialReader.ReadPluginInstallationAsync(installationId, slot, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static void AddTool(AITool tool, ICollection<AITool> target, ISet<string> toolNames)
    {
        if (!toolNames.Add(tool.Name))
        {
            throw new AgwException(
                ErrorCodes.IntegrationToolNameConflict,
                $"Integration tool name '{tool.Name}' conflicts with another capability source."
            );
        }

        target.Add(tool);
    }

    private static ConnectionCapabilityWarning StatusWarning(IntegrationConnection connection)
    {
        var code = connection.Status switch
        {
            ConnectionStatus.NeedsConfiguration => ConnectionCapabilityWarningCodes.ConnectionNeedsConfiguration,
            ConnectionStatus.PendingAuthorization => ConnectionCapabilityWarningCodes.ConnectionPendingAuthorization,
            ConnectionStatus.Unverified => ConnectionCapabilityWarningCodes.ConnectionUnverified,
            ConnectionStatus.Expired => ConnectionCapabilityWarningCodes.ConnectionExpired,
            ConnectionStatus.Invalid => ConnectionCapabilityWarningCodes.ConnectionInvalid,
            ConnectionStatus.Disabled => ConnectionCapabilityWarningCodes.ConnectionDisabled,
            ConnectionStatus.DefinitionUnavailable => ConnectionCapabilityWarningCodes.DefinitionUnavailable,
            _ => ConnectionCapabilityWarningCodes.ConnectionInvalid,
        };
        return Warning(connection.Id, code, "The integration is not ready.");
    }

    private static ConnectionCapabilityWarning SkillWarning(Guid connectionId)
    {
        return Warning(
            connectionId,
            ConnectionCapabilityWarningCodes.PluginSkillUnavailable,
            "A plugin skill is unavailable."
        );
    }

    private static ConnectionCapabilityWarning Warning(Guid connectionId, string code, string message)
    {
        return new ConnectionCapabilityWarning
        {
            ConnectionId = connectionId,
            Code = code,
            Message = message,
        };
    }

    private sealed record ReadyConnection(
        PluginDefinition Plugin,
        ConnectorDefinition Connector,
        AuthSchemeDefinition AuthScheme,
        PluginInstallation Installation
    );
}
