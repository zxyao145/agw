using Agw.Extensions.Infrastructure.Runtime;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Agw.Extensions.Tests;

public sealed class ExtensionHostIdentityTests
{
    [Fact]
    public void Identity_ReopenedAfterCleanup_PreservesHostId()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.CreateVersion7().ToString("N"));
        var paths = AgwDataPaths.Resolve(directory, directory);
        try
        {
            string hostId;
            using (var first = new ExtensionHostIdentity(paths, Options.Create(new ExtensionOptions())))
                hostId = first.HostId;
            using var second = new ExtensionHostIdentity(paths, Options.Create(new ExtensionOptions()));
            Assert.Equal(hostId, second.HostId);
            Assert.Equal(7, Guid.Parse(hostId).Version);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Identity_ConcurrentOwner_IsRejected()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.CreateVersion7().ToString("N"));
        var paths = AgwDataPaths.Resolve(directory, directory);
        try
        {
            using var first = new ExtensionHostIdentity(paths, Options.Create(new ExtensionOptions()));
            Assert.Throws<AgwException>(() => new ExtensionHostIdentity(paths, Options.Create(new ExtensionOptions())));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Identity_DistinctDataPlaneInstances_PreservesSeparateHostIdsAcrossRestart()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.CreateVersion7().ToString("N"));
        var paths = AgwDataPaths.Resolve(directory, directory);
        try
        {
            using var firstServices = CreateDataPlaneServices(paths, "first");
            var first = firstServices.GetRequiredService<ExtensionHostIdentity>();

            string secondHostId;
            using (var secondServices = CreateDataPlaneServices(paths, "second"))
            {
                var second = secondServices.GetRequiredService<ExtensionHostIdentity>();
                secondHostId = second.HostId;
                Assert.NotEqual(first.HostId, secondHostId);
                using var duplicateServices = CreateDataPlaneServices(paths, "second");
                Assert.Throws<AgwException>(() => duplicateServices.GetRequiredService<ExtensionHostIdentity>());
            }

            using var restartedServices = CreateDataPlaneServices(paths, "second");
            Assert.Equal(secondHostId, restartedServices.GetRequiredService<ExtensionHostIdentity>().HostId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("../instance")]
    [InlineData("Instance")]
    public void Identity_InvalidInstanceName_IsRejected(string instanceName)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.CreateVersion7().ToString("N"));
        var paths = AgwDataPaths.Resolve(directory, directory);
        try
        {
            using var services = CreateDataPlaneServices(paths, instanceName);
            Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<ExtensionHostIdentity>());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static ServiceProvider CreateDataPlaneServices(AgwDataPaths paths, string instanceName) =>
        new ServiceCollection()
            .AddSingleton(paths)
            .AddExtensions(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(
                        new Dictionary<string, string?> { ["Extensions:HostInstanceName"] = instanceName }
                    )
                    .Build(),
                "DataPlane"
            )
            .BuildServiceProvider();
}
