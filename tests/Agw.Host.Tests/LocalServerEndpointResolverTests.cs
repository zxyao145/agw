using Agw.Host.Runtime;

using Xunit;

namespace Agw.Host.Tests;

public class LocalServerEndpointResolverTests
{
    [Fact]
    public void ResolveDefaultUrl_PreferredPortIsAvailable_UsesPreferredPort()
    {
        var result = LocalServerEndpointResolver.ResolveDefaultUrl(
            30816,
            port => port == 30816,
            () => 49152);

        Assert.Equal("http://127.0.0.1:30816", result);
    }

    [Fact]
    public void ResolveDefaultUrl_PreferredPortIsOccupied_UsesAllocatedPort()
    {
        var result = LocalServerEndpointResolver.ResolveDefaultUrl(
            30816,
            _ => false,
            () => 49152);

        Assert.Equal("http://127.0.0.1:49152", result);
    }
}
