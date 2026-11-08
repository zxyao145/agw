using Agw.Extensions.Contracts;
using Agw.Extensions.Infrastructure.Runtime;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace Agw.Extensions.Runtime.Tests;

public sealed class ExtensionEndpointDataSourceTests
{
    [Fact]
    public void Publish_DeferredPublication_ReadsCurrentRoutesAndRemovesDisabledRoutes()
    {
        var extensionId = Guid.CreateVersion7();
        var dataSource = new ExtensionEndpointDataSource();
        using (dataSource)
        {
            IReadOnlyList<ExtensionGenerationSnapshot> current =
            [
                CreateSnapshot(extensionId, Guid.CreateVersion7(), "stale", 1),
            ];
            Func<IReadOnlyList<ExtensionGenerationSnapshot>> deferred = () => current;
            current = [CreateSnapshot(extensionId, Guid.CreateVersion7(), "current", 2)];
            dataSource.Publish(() => current);
            dataSource.Publish(deferred);

            RouteEndpoint endpoint = Assert.IsType<RouteEndpoint>(Assert.Single(dataSource.Endpoints));
            Assert.Equal("/api/ext/runtime-test/current", endpoint.RoutePattern.RawText);
            current = [];
            dataSource.Publish(deferred);
            Assert.Empty(dataSource.Endpoints);
        }
    }

    private static ExtensionGenerationSnapshot CreateSnapshot(
        Guid extensionId,
        Guid generationId,
        string endpointId,
        long epoch
    ) =>
        new()
        {
            GenerationId = new ExtensionGenerationId(extensionId, generationId),
            Name = "runtime-test",
            Version = "1.0.0",
            ReleaseHash = $"release-{epoch}",
            Epoch = epoch,
            State = ExtensionGenerationState.Active,
            Endpoints =
            [
                new ExtensionEndpointDescriptor
                {
                    Id = endpointId,
                    Method = "GET",
                    RelativePath = endpointId,
                },
            ],
            Tools = [],
            Skills = [],
        };
}
