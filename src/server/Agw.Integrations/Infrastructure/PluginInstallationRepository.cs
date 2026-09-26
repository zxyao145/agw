using Agw.Auth.Contracts;
using Agw.Integrations.Application.Management;
using Agw.Integrations.Application.Persistence;
using Agw.Integrations.Domain.Plugins;
using Agw.Integrations.Domain.Repositories;
using Agw.Integrations.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Agw.Integrations.Infrastructure;

public sealed class PluginInstallationRepository : IPluginInstallationRepository
{
    private readonly IIntegrationsDbContext _dbContext;
    private readonly IUserInfoService _userInfoService;

    public PluginInstallationRepository(IIntegrationsDbContext dbContext, IUserInfoService userInfoService)
    {
        _dbContext = dbContext;
        _userInfoService = userInfoService;
    }

    public async Task<PluginInstallationState?> FindAsync(
        ResolvedIntegrationDefinition definition,
        CancellationToken cancellationToken
    )
    {
        var ownerUserId = _userInfoService.RequiredUserId;
        var installation = await _dbContext
            .PluginInstallations.Include(item => item.Credentials)
            .FirstOrDefaultAsync(
                item => item.PluginId == definition.Plugin.Id && item.CreateBy == ownerUserId,
                cancellationToken
            );
        if (installation == null)
        {
            return null;
        }

        var scopedConfiguration = IntegrationConfigurationCodec.ReadInstallationScope(
            IntegrationConfigurationCodec.Read(installation.ConfigurationJson),
            definition.Connector.Id,
            definition.AuthScheme.Id,
            definition
                .AuthScheme.InstallationFields.Where(field => field.Type != FormFieldType.Secret)
                .Select(field => field.Id)
                .ToList()
        );
        return new PluginInstallationState { Installation = installation, ScopedConfiguration = scopedConfiguration };
    }
}
