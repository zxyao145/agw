using System.ComponentModel.DataAnnotations;
using Agw.Setup.Contracts;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.Configuration;

namespace Agw.Setup.Services;

public sealed class ConfiguredSetupBootstrap
{
    public const string SectionName = "Setup";

    private readonly SetupRequest? _request;

    private ConfiguredSetupBootstrap(SetupRequest? request)
    {
        _request = request;
    }

    public static ConfiguredSetupBootstrap None { get; } = new(null);

    public bool IsConfigured => _request != null;

    public SetupRequest Request =>
        _request ?? throw new AgwException(ErrorCodes.InvalidSetupConfiguration, "Setup bootstrap is not configured.");

    public static ConfiguredSetupBootstrap FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        if (!section.Exists())
            return None;

        string[] legacyKeys =
        [
            "DeploymentMode",
            "Provider",
            "SqlitePath",
            "PostgresHost",
            "PostgresPort",
            "PostgresDatabase",
            "PostgresUsername",
            "PostgresPassword",
        ];
        if (section.GetChildren().Any(child => legacyKeys.Contains(child.Key, StringComparer.OrdinalIgnoreCase)))
        {
            throw new AgwException(
                ErrorCodes.InvalidSetupConfiguration,
                "Setup deployment settings are no longer supported. Use Database, Execution, and DistributedLock configuration; Setup only accepts AdminPassword."
            );
        }

        var request = new SetupRequest { AdminPassword = section[nameof(SetupRequest.AdminPassword)] ?? string.Empty };
        var validationResults = new List<ValidationResult>();
        if (
            !Validator.TryValidateObject(
                request,
                new ValidationContext(request),
                validationResults,
                validateAllProperties: true
            )
        )
        {
            throw new AgwException(
                ErrorCodes.InvalidSetupConfiguration,
                $"The '{SectionName}' configuration is invalid: {string.Join(" ", validationResults.Select(result => result.ErrorMessage))}"
            );
        }

        return new ConfiguredSetupBootstrap(request);
    }
}
