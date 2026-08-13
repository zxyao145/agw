using System.ComponentModel.DataAnnotations;

using Agw.Shared.Configuration;

namespace Agw.Setup.Contracts;

public class SetupRequest : IValidatableObject
{
    [Required]
    [Display(Name = "Deployment mode")]
    public DeploymentMode DeploymentMode { get; set; } = DeploymentMode.Standalone;

    [Required]
    [Display(Name = "Database provider")]
    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Sqlite;

    [Display(Name = "SQLite database path")]
    public string SqlitePath { get; set; } = string.Empty;

    [Display(Name = "PostgreSQL host")]
    public string PostgresHost { get; set; } = string.Empty;

    [Display(Name = "PostgreSQL port")]
    public int PostgresPort { get; set; } = 5432;

    [Display(Name = "PostgreSQL database")]
    public string PostgresDatabase { get; set; } = "agw";

    [Display(Name = "PostgreSQL username")]
    public string PostgresUsername { get; set; } = string.Empty;

    [DataType(DataType.Password)]
    [Display(Name = "PostgreSQL password")]
    public string PostgresPassword { get; set; } = string.Empty;

    [Required]
    [StringLength(256, MinimumLength = 8)]
    [DataType(DataType.Password)]
    [Display(Name = "Administrator password")]
    public string AdminPassword { get; set; } = string.Empty;

    [Display(Name = "Setup Code")]
    public string? SetupCode { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enum.IsDefined(DeploymentMode))
        {
            yield return new ValidationResult(
                "Deployment mode is not supported.",
                [nameof(DeploymentMode)]);
        }

        if (!Enum.IsDefined(Provider))
        {
            yield return new ValidationResult(
                "Database provider is not supported.",
                [nameof(Provider)]);
            yield break;
        }

        if (DeploymentMode == DeploymentMode.Cluster && Provider != DatabaseProvider.Postgres)
        {
            yield return new ValidationResult(
                "Cluster deployments require PostgreSQL.",
                [nameof(Provider)]);
        }

        if (Provider == DatabaseProvider.Sqlite)
        {
            if (string.IsNullOrWhiteSpace(SqlitePath))
            {
                yield return new ValidationResult(
                    "SQLite database path is required.",
                    [nameof(SqlitePath)]);
            }

            yield break;
        }

        if (string.IsNullOrWhiteSpace(PostgresHost))
        {
            yield return new ValidationResult(
                "PostgreSQL host is required.",
                [nameof(PostgresHost)]);
        }

        if (PostgresPort is < 1 or > 65535)
        {
            yield return new ValidationResult(
                "PostgreSQL port must be between 1 and 65535.",
                [nameof(PostgresPort)]);
        }

        if (string.IsNullOrWhiteSpace(PostgresDatabase))
        {
            yield return new ValidationResult(
                "PostgreSQL database is required.",
                [nameof(PostgresDatabase)]);
        }

        if (string.IsNullOrWhiteSpace(PostgresUsername))
        {
            yield return new ValidationResult(
                "PostgreSQL username is required.",
                [nameof(PostgresUsername)]);
        }

        if (string.IsNullOrEmpty(PostgresPassword))
        {
            yield return new ValidationResult(
                "PostgreSQL password is required.",
                [nameof(PostgresPassword)]);
        }
    }
}
