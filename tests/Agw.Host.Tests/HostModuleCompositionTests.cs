using System.Reflection;
using Agw.A2A.Extensions;
using Agw.Agents;
using Agw.Agents.Contracts.Catalog;
using Agw.Agents.Contracts.Execution;
using Agw.Agents.Execution;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Auth.Extensions;
using Agw.ControlPlane.Host;
using Agw.DataPlane.Host;
using Agw.Extensions;
using Agw.Files;
using Agw.Host.Data;
using Agw.Host.Hosting;
using Agw.Infrastructure;
using Agw.Integrations.Extensions;
using Agw.Jobs;
using Agw.Projects;
using Agw.Projects.Contracts.Execution;
using Agw.Projects.Contracts.Runtime;
using Agw.Providers;
using Agw.Settings;
using Agw.Setup.Services;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Runtime;
using Agw.Skills;
using Agw.Tests;
using Agw.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Agw.Host.Tests;

public sealed class HostModuleCompositionTests
{
    [Fact]
    public void ControlPlaneExecution_RegistersSubmissionAndDiagramWithoutRuntime()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Execution:Provider"] = "Distributed",
                    ["Database:Provider"] = "postgres",
                }
            )
            .Build();
        services.AddAgentExecution(
            configuration,
            new Agw.Agents.Execution.DependencyInjection.RegistrationOptions(
                AddExecutionTransport: false,
                AddDistributedWorker: false,
                AddTraceCollector: false,
                AddRuntime: false
            )
        );
        Assert.Contains(services, d => d.ServiceType == typeof(IAgentExecutionFacade));
        Assert.Contains(services, d => d.ServiceType == typeof(IAgentflowMermaidProvider));
        Assert.DoesNotContain(
            services,
            d =>
                d.ServiceType.Name
                    is "AgentRuntimeFactory"
                        or "AgentflowRuntimeFactory"
                        or "InProcessExecutionCoordinatorFactory"
                        or "AgentflowTurnExecutor"
                        or "AgentTurnExecutor"
        );
    }

    [Fact]
    public void ControlPlaneModule_AddApplicationParts_AddsManagementControllers()
    {
        var parts = new ApplicationPartManager();

        new ControlPlaneHostModule().AddApplicationParts(parts);

        var assemblyNames = parts
            .ApplicationParts.OfType<AssemblyPart>()
            .Select(part => part.Assembly.GetName().Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("Agw.Host", assemblyNames);
        Assert.Contains("Agw.Agents", assemblyNames);
        Assert.Contains("Agw.Projects", assemblyNames);
        Assert.Contains("Agw.Setup", assemblyNames);
    }

    [Fact]
    public void DataPlaneModule_AddApplicationParts_DoesNotAddControllers()
    {
        var parts = new ApplicationPartManager();

        new DataPlaneHostModule().AddApplicationParts(parts);

        Assert.Empty(parts.ApplicationParts);
    }

    [Fact]
    public void AgentRegistration_DisabledExecutionWorkers_DoesNotRegisterHostedWorkers()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddAgentExecution(
            configuration,
            new Agw.Agents.Execution.DependencyInjection.RegistrationOptions(
                AddExecutionTransport: false,
                AddDistributedWorker: false,
                AddTraceCollector: false
            )
        );

        Assert.DoesNotContain(
            services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)),
            descriptor =>
                descriptor.ImplementationType?.Name
                    is "DistributedExecutionWorker"
                        or "AgentflowNodeExecutionTraceCollector"
        );
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType.Name == "ExecutionConnectionRegistry");
    }

    [Fact]
    public async Task AgentRuntimeFactory_ResolvedFromModuleComposition_UsesProjectsHistoryStore()
    {
        // 历史存储由 Projects 模块注册，Execution 通过 Contracts 使用同一个单例。
        // The history store is registered by the Projects module, and Execution uses the same singleton through Contracts.
        var dataPaths = AgwDataPaths.Resolve(
            Path.Combine(AppContext.BaseDirectory, ".scratch", $"agw-runtime-composition-{Guid.CreateVersion7():N}"),
            "/unused"
        );
        dataPaths.EnsureCreated();
        try
        {
            using var configuration = new ConfigurationManager();
            configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Database:ConnectionString"] = "Data Source=composition.db" }
            );
            ServerDeploymentConfiguration.Apply(configuration);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(dataPaths);
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<IHostEnvironment>(
                Microsoft
                    .Extensions.Hosting.Host.CreateApplicationBuilder(
                        new HostApplicationBuilderSettings
                        {
                            ContentRootPath = AppContext.BaseDirectory,
                            DisableDefaults = true,
                        }
                    )
                    .Environment
            );
            services.AddScoped<IEntityAuditUserIdProvider, EntityAuditUserIdProvider>();
            services.AddHybridCache();
            services
                .AddTools(configuration)
                .AddAgents(configuration)
                .AddAgentExecution(configuration)
                .AddExtensions(configuration, "Standalone")
                .AddExtensionCapabilities()
                .AddFiles(configuration)
                .AddInfrastructure(configuration)
                .AddExtensionInfrastructure()
                .AddJobs()
                .AddProviders(configuration)
                .AddSkills(configuration)
                .AddProjects(configuration)
                .AddAuth()
                .AddSettings()
                .AddSetup(configuration, readOnly: false)
                .AddIntegrations(configuration);

            await using var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var runtimeService = Assert.IsType<AgentRuntimeFactory>(
                scope.ServiceProvider.GetRequiredService<IAgentRuntimeFactory>()
            );

            var historyField = typeof(AgentRuntimeFactory).GetField(
                "_historyStore",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            Assert.NotNull(historyField);
            Assert.Same(
                scope.ServiceProvider.GetRequiredService<Agw.Projects.Contracts.History.IConversationHistoryStore>(),
                historyField.GetValue(runtimeService)
            );
            Assert.Equal("ConversationHistoryStore", historyField.GetValue(runtimeService)?.GetType().Name);
        }
        finally
        {
            Directory.Delete(dataPaths.Root, recursive: true);
        }
    }

    [Fact]
    public async Task DataPlaneModule_MapsOnlyExecutionAndA2ARoutes()
    {
        var builder = CreateValidatedBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSignalR();
        builder.Services.AddScoped<IAgentCatalogFacade, EmptyAgentCatalogFacade>();
        builder.Services.AddScoped<IExternalTaskSnapshotStore, EmptyExternalTaskSnapshotStore>();
        builder.Services.AddScoped<IProjectDefaultResolver, EmptyProjectDefaults>();
        builder.Services.AddA2A(new ConfigurationBuilder().Build());
        var app = builder.Build();

        new DataPlaneHostModule().MapEndpoints(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var patterns = app
                .Services.GetRequiredService<EndpointDataSource>()
                .Endpoints.OfType<RouteEndpoint>()
                .Select(endpoint => endpoint.RoutePattern.RawText)
                .ToArray();
            Assert.Contains("/api/hubs/exec", patterns);
            Assert.Contains("/.well-known/agents.json", patterns);
            Assert.DoesNotContain("api/jobs/list", patterns);
            Assert.DoesNotContain("setup", patterns);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public void AddJobs_Registration_AddsNoHostedService()
    {
        var services = new ServiceCollection();

        services.AddJobs();

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public async Task ControlPlaneModule_DoesNotMapExecutionOrA2ARoutes()
    {
        var builder = CreateValidatedBuilder();
        builder.WebHost.UseTestServer();
        var mvcBuilder = builder.Services.AddControllers();
        var root = Path.Combine(
            AppContext.BaseDirectory,
            ".scratch",
            $"control-plane-module-{Guid.CreateVersion7():N}"
        );
        ModuleCompositionTestHost.ConfigureServices(builder.Services, root);
        var module = new ControlPlaneHostModule();
        module.AddApplicationParts(mvcBuilder.PartManager);
        var app = builder.Build();

        try
        {
            module.MapEndpoints(app);
            await ModuleCompositionTestHost.InitializeAsync(app.Services);
            await app.StartAsync(TestContext.Current.CancellationToken);
            var patterns = app
                .Services.GetRequiredService<EndpointDataSource>()
                .Endpoints.OfType<RouteEndpoint>()
                .Select(endpoint => endpoint.RoutePattern.RawText)
                .ToArray();
            Assert.Contains("api/jobs/list", patterns);
            Assert.DoesNotContain("/api/hubs/exec", patterns);
            Assert.DoesNotContain("/.well-known/agents.json", patterns);
        }
        finally
        {
            await app.DisposeAsync();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(AgwHostProfile.ControlPlane)]
    [InlineData(AgwHostProfile.DataPlane)]
    [InlineData(AgwHostProfile.Standalone)]
    public void HostProfile_DefinesThreeFixedRoles(AgwHostProfile profile)
    {
        Assert.True(Enum.IsDefined(profile));
    }

    private static WebApplicationBuilder CreateValidatedBuilder()
    {
        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions { EnvironmentName = Environments.Development }
        );
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = true;
            options.ValidateScopes = true;
        });
        return builder;
    }

    private sealed class EmptyProjectDefaults : IProjectDefaultResolver
    {
        public Task<Guid?> ResolveDefaultProjectIdAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<Guid?>(null);

        public Task<Guid?> ResolveA2AProjectIdAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<Guid?>(null);
    }

    private sealed class EmptyAgentCatalogFacade : IAgentCatalogFacade
    {
        public Task<IReadOnlyList<AgentDescriptor>> ListDiscoverableAsync(
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyList<AgentDescriptor>>([]);

        public Task<AgentDescriptor?> FindDiscoverableByNameAsync(
            string name,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<AgentDescriptor?>(null);

        public Task<IReadOnlySet<Guid>> FilterExistingMcpServerIdsAsync(
            IReadOnlyCollection<Guid> serverIds,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

        public Task<AgentCatalogMetrics> GetMetricsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentCatalogMetrics(0, 0));

        public Task<bool> IsOwnedTargetAsync(
            Agw.Agents.Contracts.Execution.AgentRuntimeType type,
            Guid id,
            string ownerUserId,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(true);
    }

    private sealed class EmptyExternalTaskSnapshotStore : IExternalTaskSnapshotStore
    {
        public Task<ExternalTaskSnapshot?> GetAsync(
            Guid projectId,
            Guid taskId,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<ExternalTaskSnapshot?>(null);

        public Task<IReadOnlyList<ExternalTaskSnapshot>> ListAsync(
            Guid projectId,
            string? contextId,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyList<ExternalTaskSnapshot>>([]);

        public Task<ExternalTaskSaveResult> SaveAsync(
            SaveExternalTaskSnapshotRequest request,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(ExternalTaskSaveResult.Saved);

        public Task DeleteAsync(Guid projectId, Guid taskId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
