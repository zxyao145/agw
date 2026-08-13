using System.ComponentModel.DataAnnotations;

using Agw.Setup.Contracts;
using Agw.Shared.Configuration;
using Agw.Shared.Runtime;

using Microsoft.Extensions.Configuration;

namespace Agw.Setup.Services;

public sealed class ConfiguredSetupBootstrap
{
    public const string SectionName = "Setup";

    private readonly SetupRequest? _request;

    private ConfiguredSetupBootstrap(
        SetupRequest? request,
        IReadOnlyDictionary<string, string?> runtimeConfiguration)
    {
        _request = request;
        RuntimeConfiguration = runtimeConfiguration;
    }

    public static ConfiguredSetupBootstrap None { get; } = new(
        null,
        new Dictionary<string, string?>());

    public bool IsConfigured => _request != null;

    public SetupRequest Request => _request
        ?? throw new InvalidOperationException("Setup bootstrap is not configured.");

    public IReadOnlyDictionary<string, string?> RuntimeConfiguration { get; }

    public static ConfiguredSetupBootstrap FromConfiguration(
        IConfiguration configuration,
        AgwDataPaths paths)
    {
        var section = configuration.GetSection(SectionName);
        if (!section.Exists()) return None;

        SetupRequest request;
        try
        {
            request = section.Get<SetupRequest>() ?? new SetupRequest();
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"The '{SectionName}' configuration could not be parsed.",
                ex);
        }

        request.SetupCode = null;
        if (request.Provider == DatabaseProvider.Sqlite
            && string.IsNullOrWhiteSpace(request.SqlitePath))
        {
            request.SqlitePath = paths.DatabaseFile;
        }

        var validationResults = new List<ValidationResult>();
        if (!Validator.TryValidateObject(
                request,
                new ValidationContext(request),
                validationResults,
                validateAllProperties: true))
        {
            var errors = validationResults.Select(result =>
            {
                var members = string.Join(", ", result.MemberNames);
                return string.IsNullOrEmpty(members)
                    ? result.ErrorMessage
                    : $"{members}: {result.ErrorMessage}";
            });
            throw new InvalidOperationException(
                $"The '{SectionName}' configuration is invalid: {string.Join(" ", errors)}");
        }

        var connectionString = SetupConnectionStringFactory.Create(request, paths);
        var runtimeConfiguration = new Dictionary<string, string?>
        {
            ["Database:Provider"] = request.Provider == DatabaseProvider.Postgres
                ? "postgres"
                : "sqlite",
            ["Database:ConnectionString"] = connectionString,
            ["Execution:Provider"] = request.DeploymentMode == DeploymentMode.Cluster
                ? "Distributed"
                : "InProcess"
        };
        if (request.DeploymentMode == DeploymentMode.Cluster)
        {
            runtimeConfiguration["DistributedLock:Provider"] = "postgres";
            runtimeConfiguration["DistributedLock:ConnectionString"] = string.Empty;
        }

        return new ConfiguredSetupBootstrap(request, runtimeConfiguration);
    }
}
