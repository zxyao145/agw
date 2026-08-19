using Agw.Shared.Configuration;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Data;

public static class AgwDbContextOptionsConfigurator
{
    public const string SqliteMigrationsAssembly = "Agw.Migrations.Sqlite";
    public const string PostgresMigrationsAssembly = "Agw.Migrations.Postgres";

    public static void Configure(DbContextOptionsBuilder options, DatabaseProvider provider, string connectionString)
    {
        if (provider == DatabaseProvider.Postgres)
        {
            options
                .UseNpgsql(connectionString, migrations => migrations.MigrationsAssembly(PostgresMigrationsAssembly))
                .UseSnakeCaseNamingConvention();
            return;
        }

        options
            .UseSqlite(connectionString, migrations => migrations.MigrationsAssembly(SqliteMigrationsAssembly))
            .UseSnakeCaseNamingConvention();
    }
}
