using Agw.Files.Application.Files;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Files;

public static class DependencyInjection
{
    public static IServiceCollection AddFiles(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IPathSecurityService, PathSecurityService>();
        services.AddSingleton<IFilePathRequestValidator, FilePathRequestValidator>();

        return services;
    }
}
