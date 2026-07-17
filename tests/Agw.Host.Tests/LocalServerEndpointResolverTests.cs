using Agw.Host.Runtime;

using Xunit;

namespace Agw.Host.Tests;

public class LocalServerEndpointResolverTests
{
    [Fact]
    public void ResolveDefaultUrl_PreferredPortIsAvailable_UsesPreferredPort()
    {
        var result = LocalServerEndpointResolver.ResolveDefaultUrl(
            30815,
            port => port == 30815,
            () => 49152);

        Assert.Equal("http://127.0.0.1:30815", result);
    }

    [Fact]
    public void ResolveDefaultUrl_PreferredPortIsOccupied_UsesAllocatedPort()
    {
        var result = LocalServerEndpointResolver.ResolveDefaultUrl(
            30815,
            _ => false,
            () => 49152);

        Assert.Equal("http://127.0.0.1:49152", result);
    }
}
