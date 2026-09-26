using Agw.Auth.Application;
using Agw.Auth.Domain.Services;
using Agw.Auth.Infrastructure;
using Agw.Infrastructure.Auth;
using Agw.Infrastructure.Data;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Auth.Tests;

internal static class TestApiTokenStore
{
    public static ApiTokenAppService Create(AgwDbContext context) =>
        new(
            context,
            new ApiTokenNameUniquenessDomainService(new ApiTokenRepository(context)),
            new EfApiTokenCredentialReader(context),
            new UserInfoService(),
            CreateCache()
        );

    private static HybridCache CreateCache()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        return services.BuildServiceProvider().GetRequiredService<HybridCache>();
    }
}
