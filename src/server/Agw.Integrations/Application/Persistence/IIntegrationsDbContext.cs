using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Integrations;
using Microsoft.EntityFrameworkCore;

namespace Agw.Integrations.Application.Persistence;

public interface IIntegrationsDbContext : IModuleDbContext
{
    DbSet<PluginInstallation> PluginInstallations { get; }

    DbSet<PluginInstallationCredential> PluginInstallationCredentials { get; }

    DbSet<Connection> Connections { get; }

    DbSet<ConnectionCredential> ConnectionCredentials { get; }
}
