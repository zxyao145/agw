using System.ComponentModel.DataAnnotations;
using Agw.Setup.Contracts;
using Agw.Shared.Configuration;
using Xunit;

namespace Agw.Setup.Tests;

public class SetupRequestTests
{
    [Theory]
    [InlineData("1234567", false)]
    [InlineData("12345678", true)]
    public void Validate_AdminPasswordLengthBoundary_ReturnsExpectedResult(string password, bool expected)
    {
        var request = CreateValidRequest(DeploymentMode.Standalone, DatabaseProvider.Sqlite);
        request.AdminPassword = password;

        var isValid = TryValidate(request, out _);

        Assert.Equal(expected, isValid);
    }

    [Theory]
    [InlineData(DeploymentMode.Standalone, DatabaseProvider.Sqlite, true)]
    [InlineData(DeploymentMode.Standalone, DatabaseProvider.Postgres, true)]
    [InlineData(DeploymentMode.Cluster, DatabaseProvider.Postgres, true)]
    [InlineData(DeploymentMode.Cluster, DatabaseProvider.Sqlite, false)]
    public void Validate_DeploymentAndProviderCombination_ReturnsExpectedResult(
        DeploymentMode deploymentMode,
        DatabaseProvider provider,
        bool expected
    )
    {
        var request = CreateValidRequest(deploymentMode, provider);

        var isValid = TryValidate(request, out var results);

        Assert.Equal(expected, isValid);
        if (!expected)
        {
            Assert.Contains(results, result => result.MemberNames.Contains(nameof(request.Provider)));
        }
    }

    [Fact]
    public void Validate_PostgresFieldsAreMissing_ReturnsMemberErrors()
    {
        var request = CreateValidRequest(DeploymentMode.Standalone, DatabaseProvider.Postgres);
        request.PostgresHost = " ";
        request.PostgresDatabase = "";
        request.PostgresUsername = "";
        request.PostgresPassword = "";

        var isValid = TryValidate(request, out var results);

        Assert.False(isValid);
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(request.PostgresHost)));
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(request.PostgresDatabase)));
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(request.PostgresUsername)));
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(request.PostgresPassword)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Validate_PostgresPortIsOutsideRange_ReturnsPortError(int port)
    {
        var request = CreateValidRequest(DeploymentMode.Cluster, DatabaseProvider.Postgres);
        request.PostgresPort = port;

        var isValid = TryValidate(request, out var results);

        Assert.False(isValid);
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(request.PostgresPort)));
    }

    [Fact]
    public void Validate_EnumsAreUnsupported_ReturnsModeAndProviderErrors()
    {
        var request = CreateValidRequest(DeploymentMode.Standalone, DatabaseProvider.Sqlite);
        request.DeploymentMode = (DeploymentMode)99;
        request.Provider = (DatabaseProvider)99;

        var isValid = TryValidate(request, out var results);

        Assert.False(isValid);
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(request.DeploymentMode)));
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(request.Provider)));
    }

    private static SetupRequest CreateValidRequest(DeploymentMode deploymentMode, DatabaseProvider provider)
    {
        return new SetupRequest
        {
            DeploymentMode = deploymentMode,
            Provider = provider,
            SqlitePath = "/data/agw.db",
            PostgresHost = "db.internal",
            PostgresPort = 5432,
            PostgresDatabase = "agw",
            PostgresUsername = "agw",
            PostgresPassword = "database-password",
            AdminPassword = "administrator-password",
        };
    }

    private static bool TryValidate(SetupRequest request, out List<ValidationResult> results)
    {
        results = [];
        return Validator.TryValidateObject(
            request,
            new ValidationContext(request),
            results,
            validateAllProperties: true
        );
    }
}
