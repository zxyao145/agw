using Agw.Files.Abstracts;
using Agw.Files.Application.Files;
using Agw.Files.Application.Storage.Local;
using Agw.Files.Application.Storage.Resolver;
using Agw.Files.Application.Storage.Sftp;
using Agw.Files.Services;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agw.Files;

public static class DependencyInjection
{
    public static IServiceCollection AddFiles(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<FileAppService>();
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<LocalFileSystemFactory>();
        services.AddSingleton<SftpFileSystemFactory>();
        services.AddSingleton<IAgwFileSystemResolver, ProjectScopedFileSystemResolver>();
        services.AddSingleton<IGitCommandService, GitCommandService>();

        return services;
    }
}
