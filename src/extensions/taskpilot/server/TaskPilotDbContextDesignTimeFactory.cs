using Agw.Extensions.Sdk.Database;
using Agw.Extensions.Sdk.EntityFrameworkCore;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Agw.Extensions.TaskPilot;

public sealed class TaskPilotDbContextDesignTimeFactory : IDesignTimeDbContextFactory<TaskPilotDbContext>
{
    private static readonly Guid GenerationId = Guid.Parse("019a4c17-5f00-7a80-8b3d-2b732f48a102");
    private static readonly TaskPilotDesignTimeUserContext UserContext = new();

    public TaskPilotDbContext CreateDbContext(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string provider = ReadProvider(args);
        var options = new DbContextOptionsBuilder<TaskPilotDbContext>();
        string assembly = typeof(TaskPilotMigrationsMarker).Assembly.GetName().Name!;
        if (string.Equals(provider, "sqlite", StringComparison.OrdinalIgnoreCase))
        {
            options.UseSqlite(
                "Data Source=taskpilot-migrations.db",
                configuration => configuration.MigrationsAssembly(assembly)
            );
        }
        else if (string.Equals(provider, "postgres", StringComparison.OrdinalIgnoreCase))
        {
            options.UseNpgsql(
                "Host=127.0.0.1;Database=taskpilot_migrations;Username=postgres;Password=postgres",
                configuration => configuration.MigrationsAssembly(assembly)
            );
        }
        else
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Specify --provider sqlite or --provider postgres.");
        }

        options.UseSnakeCaseNamingConvention().ReplaceService<IMigrationsAssembly, ExtensionMigrationsAssembly>();
        return new TaskPilotDbContext(
            options.Options,
            new ExtensionDatabaseIdentity(TaskPilotExtension.ExtensionId, GenerationId, 0),
            UserContext,
            TimeProvider.System
        );
    }

    private static string ReadProvider(IReadOnlyList<string> args)
    {
        for (int index = 0; index + 1 < args.Count; index++)
        {
            if (string.Equals(args[index], "--provider", StringComparison.Ordinal))
            {
                return args[index + 1];
            }
        }

        throw new AgwException(ErrorCodes.InvalidParam, "Specify --provider sqlite or --provider postgres.");
    }

    private sealed class TaskPilotDesignTimeUserContext : IExtensionUserContext
    {
        public string? UserId => "taskpilot-migrations";

        public Guid? InvocationId => null;
    }
}
