using Agw.Shared.Configuration;

namespace Agw.Infrastructure.Configuration;

public class DatabaseSettings
{
    public const string SectionName = "Database";
    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Sqlite;
    public string ConnectionString { get; set; } = string.Empty;
}
