using Agw.A2A.Extensions;
using Agw.Agents;
using Agw.ControlPlane.Host;
using Agw.DataPlane.Host;
using Agw.Host.Hosting;
using Agw.Jobs;
using Agw.Jobs.Execution;
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
    public void JobRegistration_DurableScheduler_UsesDurableExecutor()
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
                && descriptor.ImplementationType == typeof(DurableJobAgentExecutor)
        );
        Assert.DoesNotContain(
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
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSignalR();
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
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var mvcBuilder = builder.Services.AddControllers();
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
}
