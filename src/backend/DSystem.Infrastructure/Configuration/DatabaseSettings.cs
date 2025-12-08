namespace DSystem.Infrastructure.Configuration;

public class DatabaseSettings
{
    public const string SectionName = "Database";
    public string Provider { get; set; } = "sqlite";
    public string ConnectionString { get; set; } = string.Empty;
}
