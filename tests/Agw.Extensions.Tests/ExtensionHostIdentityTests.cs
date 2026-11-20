using Agw.Extensions.Infrastructure.Runtime;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
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
}
