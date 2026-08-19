using Agw.Integrations.Application.Capabilities;
using Agw.Integrations.Application.Plugins;
using Agw.Integrations.Contracts.Management;
using Agw.Integrations.Domain.Plugins;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Integrations.Application.Management;

public sealed class PluginCatalogAppService
{
    private readonly IPluginCatalog _pluginCatalog;
    private readonly IRepository<PluginInstallation>? _installationRepository;
    private readonly PluginSkillMetadataReader _pluginSkillMetadataReader;

    public PluginCatalogAppService(IPluginCatalog pluginCatalog, PluginSkillMetadataReader pluginSkillMetadataReader)
    {
        _pluginCatalog = pluginCatalog;
        _pluginSkillMetadataReader = pluginSkillMetadataReader;
    }

    public PluginCatalogAppService(
        IPluginCatalog pluginCatalog,
        IRepository<PluginInstallation> installationRepository,
        PluginSkillMetadataReader pluginSkillMetadataReader
    )
    {
        _pluginCatalog = pluginCatalog;
        _installationRepository = installationRepository;
        _pluginSkillMetadataReader = pluginSkillMetadataReader;
    }

    public IReadOnlyList<PluginResponse> List()
    {
        return _pluginCatalog.List().Select(plugin => MapPlugin(plugin, null)).ToList();
    }

    public async Task<IReadOnlyList<PluginResponse>> ListAsync(CancellationToken cancellationToken)
    {
        var installations =
            _installationRepository == null
                ? []
                : await _installationRepository
                    .Queryable.Include(installation => installation.Credentials)
                    .ToListAsync(cancellationToken);
        return _pluginCatalog
            .List()
            .Select(plugin =>
                MapPlugin(
                    plugin,
                    installations.FirstOrDefault(installation =>
                        string.Equals(installation.PluginId, plugin.Id, StringComparison.OrdinalIgnoreCase)
                    )
                )
            )
            .ToList();
    }

    private PluginResponse MapPlugin(PluginDefinition plugin, PluginInstallation? installation) =>
        new()
        {
            Id = plugin.Id,
            Version = plugin.Version,
            DisplayName = plugin.DisplayName,
            Description = plugin.Description,
            Tags = plugin.Tags,
            Connectors = plugin.Connectors.Select(connector => MapConnector(connector, installation)).ToList(),
            Skills = plugin.Skills.Select(skill => MapSkill(skill)).OfType<PluginSkillResponse>().ToList(),
        };

    private PluginSkillResponse? MapSkill(PluginSkillDefinition skill)
    {
        if (!_pluginSkillMetadataReader.TryRead(skill, out var metadata))
        {
            return null;
        }

        return new PluginSkillResponse
        {
            Id = metadata.Id,
            Description = metadata.Description,
            ContentPath = skill.ContentPath,
        };
    }

    private static ConnectorResponse MapConnector(ConnectorDefinition connector, PluginInstallation? installation) =>
        new()
        {
            Id = connector.Id,
            DisplayName = connector.DisplayName,
            Description = connector.Description,
            AuthSchemes = connector
                .AuthSchemes.Select(authScheme => MapAuthScheme(connector, authScheme, installation))
                .ToList(),
            CapabilitySources = connector.CapabilitySources.Select(MapCapabilitySource).ToList(),
        };

    private static AuthSchemeResponse MapAuthScheme(
        ConnectorDefinition connector,
        AuthSchemeDefinition authScheme,
        PluginInstallation? installation
    ) =>
        new()
        {
            Id = authScheme.Id,
            DisplayName = authScheme.DisplayName,
            Type = (AuthSchemeTypeResponse)authScheme.Type,
            OAuth2AuthorizationCode =
                authScheme.OAuth2AuthorizationCode == null ? null : MapOAuth(authScheme.OAuth2AuthorizationCode),
            InstallationFields = authScheme.InstallationFields.Select(MapField).ToList(),
            ConnectionFields = authScheme.ConnectionFields.Select(MapField).ToList(),
            Installation = installation == null ? null : MapInstallation(connector, authScheme, installation),
        };

    private static PluginInstallationScopeResponse MapInstallation(
        ConnectorDefinition connector,
        AuthSchemeDefinition authScheme,
        PluginInstallation installation
    )
    {
        var allConfiguration = IntegrationConfigurationCodec.Read(installation.ConfigurationJson);
        var nonSecretFieldIds = authScheme
            .InstallationFields.Where(field => field.Type != FormFieldType.Secret)
            .Select(field => field.Id)
            .ToList();
        var configuration = IntegrationConfigurationCodec.ReadInstallationScope(
            allConfiguration,
            connector.Id,
            authScheme.Id,
            nonSecretFieldIds
        );
        var secrets = authScheme
            .InstallationFields.Where(field => field.Type == FormFieldType.Secret)
            .ToDictionary(
                field => field.Id,
                field =>
                {
                    var slot = IntegrationCredentialSlots.InstallationField(connector.Id, authScheme.Id, field.Id);
                    var credential = installation.Credentials.FirstOrDefault(item =>
                        string.Equals(item.Slot, slot, StringComparison.OrdinalIgnoreCase)
                    );
                    return new SecretFieldStateResponse { Configured = credential != null };
                },
                StringComparer.OrdinalIgnoreCase
            );

        return new PluginInstallationScopeResponse
        {
            Id = installation.Id,
            Enabled = installation.Enabled,
            Configuration = configuration,
            Secrets = secrets,
        };
    }

    private static FormFieldResponse MapField(FormFieldDefinition field) =>
        new()
        {
            Id = field.Id,
            Label = field.Label,
            Type = (FormFieldTypeResponse)field.Type,
            IsRequired = field.IsRequired,
            Description = field.Description,
        };

    private static OAuth2AuthorizationCodeResponse MapOAuth(OAuth2AuthorizationCodeSettings oauth) =>
        new()
        {
            AuthorizationEndpoint = oauth.AuthorizationEndpoint,
            TokenEndpoint = oauth.TokenEndpoint,
            UserInfoEndpoint = oauth.UserInfoEndpoint,
            ClientIdFieldId = oauth.ClientIdFieldId,
            ClientSecretFieldId = oauth.ClientSecretFieldId,
            SubjectResolution = new OAuthSubjectResolutionResponse
            {
                Source = (OAuthSubjectSourceResponse)oauth.SubjectResolution.Source,
                Field = oauth.SubjectResolution.Field,
            },
            UsePkce = oauth.UsePkce,
            ClientAuthenticationMethod = (OAuth2ClientAuthenticationMethodResponse)oauth.ClientAuthenticationMethod,
            SupportsRefresh = oauth.SupportsRefresh,
            Scopes = oauth.Scopes,
            AdditionalAuthorizeParameters = oauth.AdditionalAuthorizeParameters,
            AdditionalTokenParameters = oauth.AdditionalTokenParameters,
        };

    private static CapabilitySourceResponse MapCapabilitySource(CapabilitySourceDefinition source)
    {
        return source switch
        {
            NativeCapabilitySourceDefinition native => new CapabilitySourceResponse
            {
                Id = native.Id,
                Kind = CapabilitySourceKindResponse.Native,
                Provider = native.Provider,
            },
            McpCapabilitySourceDefinition mcp => new CapabilitySourceResponse
            {
                Id = mcp.Id,
                Kind = CapabilitySourceKindResponse.Mcp,
                McpTransport = MapTransport(mcp.Transport),
                CredentialBindings = mcp.CredentialBindings.Select(MapCredentialBinding).ToList(),
            },
            _ => throw new AgwException(ErrorCodes.IntegrationDataInvalid),
        };
    }

    private static McpTransportResponse MapTransport(McpTransportDefinition transport)
    {
        return transport switch
        {
            StdioMcpTransportDefinition stdio => new McpTransportResponse
            {
                Kind = McpTransportKindResponse.Stdio,
                Command = stdio.Command,
                Arguments = stdio.Arguments,
            },
            HttpMcpTransportDefinition http => new McpTransportResponse
            {
                Kind = McpTransportKindResponse.Http,
                Endpoint = http.Endpoint,
            },
            SseMcpTransportDefinition sse => new McpTransportResponse
            {
                Kind = McpTransportKindResponse.Sse,
                Endpoint = sse.Endpoint,
            },
            _ => throw new AgwException(ErrorCodes.IntegrationDataInvalid),
        };
    }

    private static CredentialBindingResponse MapCredentialBinding(CredentialBindingDefinition binding)
    {
        var (kind, fieldId) = binding.ValueSource switch
        {
            ConnectionFieldCredentialValueSourceDefinition connection => (
                CredentialValueSourceKindResponse.ConnectionField,
                connection.FieldId
            ),
            InstallationFieldCredentialValueSourceDefinition installation => (
                CredentialValueSourceKindResponse.InstallationField,
                installation.FieldId
            ),
            OAuthAccessTokenCredentialValueSourceDefinition => (
                CredentialValueSourceKindResponse.OAuthAccessToken,
                null
            ),
            _ => throw new AgwException(ErrorCodes.IntegrationDataInvalid),
        };

        return new CredentialBindingResponse
        {
            SourceKind = kind,
            AuthSchemeId = binding.ValueSource.AuthSchemeId,
            FieldId = fieldId,
            Target = (CredentialBindingTargetResponse)binding.Target,
            TargetName = binding.TargetName,
            ValuePrefix = binding.ValuePrefix,
        };
    }
}
