using Agw.Agents.Execution.Agents.Composition;
using Agw.Agents.Execution.Turns;
using Agw.Auth.Extensions;
using Agw.Extensions.Contracts;
using Agw.Extensions.Infrastructure.Runtime;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Settings;
using Agw.Settings;
using Agw.Settings.Application.Persistence;
using Agw.Settings.Contracts;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Agents.Tests;

public sealed class TurnAcceptanceLockTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Registration_MissingExtensionCatalog_FailsBeforeOptionalConstruction(bool composer)
    {
        var services = new ServiceCollection();
        services.AddAgentExecution(
            new ConfigurationBuilder().Build(),
            new Agw.Agents.Execution.DependencyInjection.RegistrationOptions(
                AddExecutionTransport: false,
                AddDistributedWorker: false,
                AddTraceCollector: false
            )
        );
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            if (composer)
                scope.ServiceProvider.GetRequiredService<AgentCapabilityComposer>();
            else
                scope.ServiceProvider.GetRequiredService<TurnAcceptanceService>();
        });
        Assert.Contains(typeof(IExtensionCapabilityCatalog).FullName!, error.Message);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Postgres_TurnAcceptance_EnabledExtensionAdmissionBlocksDisableUntilCommit()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Agw.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        var configurationPath = Path.Combine(root.FullName, ".scratch", "postgres-tests.json");
        if (!File.Exists(configurationPath))
            Assert.Skip("PostgreSQL test configuration is not present.");
        var configuration = new ConfigurationBuilder().AddJsonFile(configurationPath).Build();
        var connectionString = configuration["ConnectionString"]!;
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var kit = await TurnPersistenceTestKit.CreatePostgresAsync(
            connectionString,
            configure: services =>
            {
                services.AddAuth();
                services.AddSettings();
                services.AddScoped<ISettingsDbContext>(provider => provider.GetRequiredService<AgwDbContext>());
                services.AddScoped<ISettingsPersistence, EfSettingsPersistence>();
                services.AddScoped<IExtensionAdmissionValidator, ExtensionAdmissionValidator>();
            }
        );
        var extensionId = Guid.CreateVersion7();
        var key = $"extensions.enabled.{extensionId:D}";
        await using (var setup = kit.Services.CreateAsyncScope())
        {
            Assert.True(
                await setup
                    .ServiceProvider.GetRequiredService<ISettingsStore>()
                    .TryCreateGlobalAsync(key, new EnablementSetting { Enabled = true }, cancellationToken)
            );
        }

        await using var admissionScope = kit.Services.CreateAsyncScope();
        var admissionDb = admissionScope.ServiceProvider.GetRequiredService<AgwDbContext>();
        await using var transaction = await admissionDb.Database.BeginTransactionAsync(cancellationToken);
        await admissionScope
            .ServiceProvider.GetRequiredService<IExtensionAdmissionValidator>()
            .ValidateEnabledAsync([extensionId], cancellationToken);

        await using var disableScope = kit.Services.CreateAsyncScope();
        Task<bool> disableTask = disableScope
            .ServiceProvider.GetRequiredService<ISettingsStore>()
            .TryUpdateGlobalAsync(key, new EnablementSetting { Enabled = false }, 1, cancellationToken);
        await Assert.ThrowsAsync<TimeoutException>(() =>
            disableTask.WaitAsync(TimeSpan.FromMilliseconds(250), cancellationToken)
        );
        await transaction.CommitAsync(cancellationToken);
        Assert.True(await disableTask);
        await using var afterCommit = kit.Services.CreateAsyncScope();
        var unavailable = await Assert.ThrowsAsync<AgwException>(() =>
            afterCommit
                .ServiceProvider.GetRequiredService<IExtensionAdmissionValidator>()
                .ValidateEnabledAsync([extensionId], cancellationToken)
        );
        Assert.Equal(ErrorCodes.ExtensionUnavailable.Code, unavailable.Code);
    }

    private sealed class EnablementSetting
    {
        public EnablementSetting() { }

        public bool Enabled { get; init; }
    }
}
