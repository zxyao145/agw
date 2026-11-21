using Agw.Jobs.Api;
using Agw.Jobs.Application.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agw.Jobs.Tests;

public class EndpointExtensionTests
{
    [Fact]
    public async Task MapJobsApi_MapsExpectedRoutes()
    {
        // This test inspects route metadata only; JobsApiTests covers the complete service composition.
        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions { EnvironmentName = Environments.Production }
        );
        builder.Services.AddScoped<JobAppService>();

        await using var app = builder.Build();
        app.MapJobsApi();

        var routeBuilder = (IEndpointRouteBuilder)app;
        var patterns = routeBuilder
            .DataSources.SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "api/jobs/create",
                "api/jobs/delete",
                "api/jobs/enable",
                "api/jobs/get",
                "api/jobs/list",
                "api/jobs/runs/list",
                "api/jobs/update",
            ],
            patterns
        );
    }
}
