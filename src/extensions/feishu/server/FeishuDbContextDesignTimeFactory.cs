using Agw.Extensions.Sdk.Database;
using Agw.Extensions.Sdk.EntityFrameworkCore;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Agw.Extensions.Feishu;

public sealed class FeishuDbContextDesignTimeFactory : IDesignTimeDbContextFactory<FeishuDbContext>
{
    private static readonly Guid GenerationId = Guid.Parse("019a4c17-5f00-7a80-8b3d-2b732f48f113");
    private static readonly FeishuDesignTimeUserContext UserContext = new();

    public FeishuDbContext CreateDbContext(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string provider = ReadProvider(args);
        var options = new DbContextOptionsBuilder<FeishuDbContext>();
        string assembly = typeof(FeishuMigrationsMarker).Assembly.GetName().Name!;
        if (string.Equals(provider, "sqlite", StringComparison.OrdinalIgnoreCase))
        {
            options
                .UseSqlite(
                    "Data Source=feishu-migrations.db",
                    configuration => configuration.MigrationsAssembly(assembly)
                )
                .UseSnakeCaseNamingConvention();
        }
        else if (string.Equals(provider, "postgres", StringComparison.OrdinalIgnoreCase))
        {
            options
                .UseNpgsql(
                    "Host=127.0.0.1;Database=feishu_migrations;Username=postgres;Password=postgres",
                    configuration => configuration.MigrationsAssembly(assembly)
                )
                .UseSnakeCaseNamingConvention();
        }
        else
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Specify --provider sqlite or --provider postgres.");
        }

        options.UseSnakeCaseNamingConvention().ReplaceService<IMigrationsAssembly, ExtensionMigrationsAssembly>();

        return new FeishuDbContext(
            options.Options,
            new ExtensionDatabaseIdentity(FeishuExtension.ExtensionId, GenerationId, 0),
            UserContext,
            TimeProvider.System
        );
    }

    private static string ReadProvider(IReadOnlyList<string> args)
    {
        for (int index = 0; index + 1 < args.Count; index++)
        {
            if (string.Equals(args[index], "--provider", StringComparison.Ordinal))
                return args[index + 1];
        }

        throw new AgwException(ErrorCodes.InvalidParam, "Specify --provider sqlite or --provider postgres.");
    }

    private sealed class FeishuDesignTimeUserContext : IExtensionUserContext
    {
        public string? UserId => "feishu-migrations";

        public Guid? InvocationId => null;
    }
}
