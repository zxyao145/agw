using Agw.Setup.Contracts;
using Agw.Setup.Services;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Agw.Setup.Tests;

public sealed class ConfiguredSetupBootstrapTests
{
    [Fact]
    public void FromConfiguration_WhenSetupSectionIsMissing_ReturnsNotConfigured()
    {
        var bootstrap = ConfiguredSetupBootstrap.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.False(bootstrap.IsConfigured);
    }

    [Fact]
    public void FromConfiguration_WithPasswordOnly_DoesNotRequireDeploymentFieldsOrReadSetupCode()
    {
        var configuration = CreateConfiguration(
            new Dictionary<string, string?>
            {
                ["Setup:AdminPassword"] = "administrator-password",
                ["Setup:SetupCode"] = "ignored-code",
            }
        );

        var bootstrap = ConfiguredSetupBootstrap.FromConfiguration(configuration);

        Assert.True(bootstrap.IsConfigured);
        Assert.Equal("administrator-password", bootstrap.Request.AdminPassword);
        Assert.Null(bootstrap.Request.SetupCode);
    }

    [Theory]
    [InlineData("DeploymentMode")]
    [InlineData("Provider")]
    [InlineData("SqlitePath")]
    [InlineData("PostgresHost")]
    [InlineData("PostgresPort")]
    [InlineData("PostgresDatabase")]
    [InlineData("PostgresUsername")]
    [InlineData("PostgresPassword")]
    public void FromConfiguration_WithLegacyDeploymentField_RejectsWithoutLeakingValues(string key)
    {
        var configuration = CreateConfiguration(
            new Dictionary<string, string?>
            {
                ["Setup:AdminPassword"] = "administrator-password",
                [$"Setup:{key}"] = "legacy-secret-value",
            }
        );

        var exception = Assert.Throws<AgwException>(() => ConfiguredSetupBootstrap.FromConfiguration(configuration));

        Assert.Equal(ErrorCodes.InvalidSetupConfiguration.Code, exception.Code);
        Assert.Contains("Database, Execution, and DistributedLock", exception.Message);
        Assert.DoesNotContain("administrator-password", exception.Message);
        Assert.DoesNotContain("legacy-secret-value", exception.Message);
    }

    [Fact]
    public void FromConfiguration_WithInvalidPassword_RejectsWithoutLeakingPassword()
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?> { ["Setup:AdminPassword"] = "short" });

        var exception = Assert.Throws<AgwException>(() => ConfiguredSetupBootstrap.FromConfiguration(configuration));

        Assert.Equal(ErrorCodes.InvalidSetupConfiguration.Code, exception.Code);
        Assert.DoesNotContain("short", exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializeIfConfiguredAsync_RespectsExistingInitialization(bool isInitialized)
    {
        var bootstrap = ConfiguredSetupBootstrap.FromConfiguration(
            CreateConfiguration(new Dictionary<string, string?> { ["Setup:AdminPassword"] = "administrator-password" })
        );
        var setupService = new StubSetupInitializationService();
        var initializer = new ConfiguredSetupInitializer(
            new StubInitializationStateStore(isInitialized),
            setupService,
            bootstrap,
            NullLogger<ConfiguredSetupInitializer>.Instance,
            new ConfigurationBuilder().Build()
        );

        var initialized = await initializer.InitializeIfConfiguredAsync(TestContext.Current.CancellationToken);

        Assert.Equal(!isInitialized, initialized);
        Assert.Equal(isInitialized ? null : bootstrap.Request, setupService.LastRequest);
    }

    private static IConfiguration CreateConfiguration(IReadOnlyDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private sealed class StubInitializationStateStore : IInitializationStateStore
    {
        public StubInitializationStateStore(bool isInitialized)
        {
            IsInitialized = isInitialized;
        }

        public bool IsInitialized { get; }

        public Task PersistAsync(string passwordHash, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class StubSetupInitializationService : ISetupInitializationService
    {
        public SetupRequest? LastRequest { get; private set; }

        public Task InitializeAsync(SetupRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.CompletedTask;
        }
    }
}
