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
        var routes = routeBuilder
            .DataSources.SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint =>
                (
                    Pattern: endpoint.RoutePattern.RawText,
                    Method: endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Single()
                )
            )
            .OrderBy(route => route.Pattern)
            .ThenBy(route => route.Method)
            .ToArray();

        Assert.Equal(
            [
                ("api/jobs", "GET"),
                ("api/jobs", "POST"),
                ("api/jobs/{id:guid}", "DELETE"),
                ("api/jobs/{id:guid}", "GET"),
                ("api/jobs/{id:guid}", "PUT"),
                ("api/jobs/{id:guid}/logs", "GET"),
                ("api/jobs/enabled", "PUT"),
            ],
            routes
        );
    }
}
