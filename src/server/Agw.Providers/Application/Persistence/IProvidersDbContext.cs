using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Providers;
using Microsoft.EntityFrameworkCore;

namespace Agw.Providers.Application.Persistence;

public interface IProvidersDbContext : IModuleDbContext
{
    DbSet<Provider> Providers { get; }

    DbSet<ProviderAuthConfig> ProviderAuthConfigs { get; }

    DbSet<AgwAiModel> Models { get; }

    DbSet<ModelProviderRelation> ModelProviders { get; }
}
