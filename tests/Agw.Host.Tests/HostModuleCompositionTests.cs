using Agw.A2A.Extensions;
using Agw.Agents;
using Agw.Agents.Contracts.Catalog;
using Agw.Agents.Contracts.Execution;
using Agw.Auth.Contracts;
using Agw.ControlPlane.Host;
using Agw.DataPlane.Host;
using Agw.Host.Hosting;
using Agw.Jobs;
using Agw.Jobs.Application.Persistence;
using Agw.Jobs.Execution;
using Agw.Jobs.Scheduling;
using Agw.Jobs.Scheduling.Coordination;
using Agw.Projects.Contracts.Execution;
using Agw.Projects.Contracts.Runtime;
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

        services.AddAgents(
            configuration,
            new Agw.Agents.DependencyInjection.RegistrationOptions(
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
    public void JobRegistration_DisabledScheduler_DoesNotRegisterJobHostedService()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddJobs(configuration, new Agw.Jobs.DependencyInjection.RegistrationOptions(AddScheduler: false));

        Assert.DoesNotContain(
            services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)),
            descriptor => descriptor.ImplementationType?.Name == "JobHostedService"
        );
    }

    [Fact]
    public void JobRegistration_DurableScheduler_UsesTopologyNeutralExecutor()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddJobs(
            configuration,
            new Agw.Jobs.DependencyInjection.RegistrationOptions(AddScheduler: true, UseDurableExecution: true)
        );

        Assert.Contains(
            services,
            descriptor =>
                descriptor.ServiceType == typeof(IJobAgentExecutor)
                && descriptor.ImplementationType == typeof(JobAgentExecutor)
        );
        Assert.Contains(
            services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)),
            descriptor => descriptor.ImplementationType?.Name == "DurableJobRecoveryHostedService"
        );
    }

    [Fact]
    public async Task DataPlaneModule_MapsOnlyExecutionAndA2ARoutes()
    {
        var builder = CreateValidatedBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSignalR();
        builder.Services.AddScoped<IAgentCatalogFacade, EmptyAgentCatalogFacade>();
        builder.Services.AddScoped<IExternalTaskSnapshotStore, EmptyExternalTaskSnapshotStore>();
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
            Assert.DoesNotContain("api/jobs", patterns);
            Assert.DoesNotContain("setup", patterns);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task ControlPlaneModule_DoesNotMapExecutionOrA2ARoutes()
    {
        var builder = CreateValidatedBuilder();
        builder.WebHost.UseTestServer();
        var mvcBuilder = builder.Services.AddControllers();
        AddControlPlaneTestDependencies(builder.Services);
        builder.Services.AddJobs(
            new ConfigurationBuilder().Build(),
            new Agw.Jobs.DependencyInjection.RegistrationOptions(AddScheduler: false)
        );
        var module = new ControlPlaneHostModule();
        module.AddApplicationParts(mvcBuilder.PartManager);
        var app = builder.Build();

        module.MapEndpoints(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var patterns = app
                .Services.GetRequiredService<EndpointDataSource>()
                .Endpoints.OfType<RouteEndpoint>()
                .Select(endpoint => endpoint.RoutePattern.RawText)
                .ToArray();
            Assert.Contains("api/jobs", patterns);
            Assert.DoesNotContain("/api/hubs/exec", patterns);
            Assert.DoesNotContain("/.well-known/agents.json", patterns);
        }
        finally
        {
            await app.DisposeAsync();
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

    private static void AddControlPlaneTestDependencies(IServiceCollection services)
    {
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<JobScheduleCalculator>();
        services.AddSingleton<JobSchedulerWakeSignal>();
        services.AddSingleton<ICurrentAgentTurn, EmptyCurrentAgentTurn>();
        services.AddScoped<IUserInfoService, TestUserInfoService>();
        services.AddScoped<IProjectRuntimeFacade, EmptyProjectRuntimeFacade>();
        services.AddScoped<IAgentCatalogFacade, EmptyAgentCatalogFacade>();
        services.AddScoped<IJobsDbContext>(_ => null!);
        services.AddScoped<IProjectTaskFacade, EmptyProjectTaskFacade>();
    }

    private sealed class EmptyCurrentAgentTurn : ICurrentAgentTurn
    {
        public AgentTurnSnapshot? Current => null;
    }

    private sealed class EmptyProjectRuntimeFacade : IProjectRuntimeFacade
    {
        public Task<ProjectRuntimeSnapshot?> GetForCurrentUserAsync(
            Guid projectId,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<ProjectRuntimeSnapshot?>(null);

        public Task<string?> GetWorkspaceAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    private sealed class EmptyProjectTaskFacade : IProjectTaskFacade
    {
        public Task<int?> GetGenerationAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(0);

        public Task<ProjectTaskSnapshot> ResolveAsync(
            ResolveProjectTaskRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<ProjectTaskSnapshot?> GetAsync(Guid taskId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ProjectTaskSnapshot?>(null);

        public Task<ProjectTaskSnapshot> GetOrCreateAsync(
            StartProjectTaskRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<ProjectTaskSnapshot?> FinishAsync(
            FinishProjectTaskRequest request,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<ProjectTaskSnapshot?>(null);

        public Task<IReadOnlyDictionary<Guid, string?>> ResolveContextIdsAsync(
            IReadOnlyCollection<Guid> taskIds,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyDictionary<Guid, string?>>(new Dictionary<Guid, string?>());
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
