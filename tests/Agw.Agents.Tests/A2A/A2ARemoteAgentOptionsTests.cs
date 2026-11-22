using Agw.Agents.ExternalAgents.A2A;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agw.Agents.Tests.A2A;

public sealed class A2ARemoteAgentOptionsTests
{
    [Fact]
    public void Bind_DefaultSection_UsesDocumentedDefaults()
    {
        using var services = new ServiceCollection()
            .AddAgents(new ConfigurationBuilder().Build())
            .BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<A2ARemoteAgentOptions>>().Value;

        Assert.True(options.AllowPrivateNetworks);
        Assert.Equal(TimeSpan.FromSeconds(15), options.CardFetchTimeout);
        Assert.Equal(1048576, options.CardMaxBytes);
        Assert.Equal(TimeSpan.FromMinutes(1), options.RequestTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), options.StreamIdleTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), options.PollInterval);
        Assert.Equal(3, options.ResubscribeAttempts);
    }
}
