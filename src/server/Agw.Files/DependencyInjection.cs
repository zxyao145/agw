using Agw.Files.Application.Files;
using Agw.Files.Application.Storage.Local;
using Agw.Files.Application.Storage.Resolver;
using Agw.Files.Application.Storage.Sftp;

using Agw.Shared.Contracts.Storage;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Files;

public static class DependencyInjection
{
    public static IServiceCollection AddFiles(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IPathSecurityService, PathSecurityService>();
        services.AddSingleton<IFilePathRequestValidator, FilePathRequestValidator>();

        services.AddSingleton<LocalFileSystemFactory>();
        services.AddSingleton<SftpFileSystemFactory>();
        services.AddSingleton<IAgwFileSystemResolver, ProjectScopedFileSystemResolver>();

        return services;
    }
}
