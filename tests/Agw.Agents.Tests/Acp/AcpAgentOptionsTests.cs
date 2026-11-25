using Agw.Agents.Definitions.Domain.Rules;
using Agw.Agents.ExternalAgents.Acp;
using Agw.Shared.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agw.Agents.Tests.Acp;

public sealed class AcpAgentOptionsTests
{
    [Fact]
    public void Bind_DefaultSection_UsesDocumentedDefaults()
    {
        var defaults = ReadOptions([]);
        var root = Path.Combine(AppContext.BaseDirectory, "acp-data");
        var dataPaths = AgwDataPaths.Resolve(root, root);

        Assert.Equal(
            "https://cdn.agentclientprotocol.com/registry/v1/latest/registry.json",
            defaults.RegistryUrl.AbsoluteUri
        );
        Assert.Equal(TimeSpan.FromHours(1), defaults.RegistryRefreshInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), defaults.RegistryFetchTimeout);
        Assert.Equal(8_388_608, defaults.RegistryMaxBytes);
        Assert.Equal(Path.Combine(root, "acp-agents"), defaults.ResolveInstallRoot(dataPaths));
        Assert.False(defaults.AllowUnverifiedBinaries);
        Assert.Equal(TimeSpan.FromMinutes(10), defaults.DownloadTimeout);
        Assert.Equal(1_073_741_824, defaults.MaxDownloadBytes);
        Assert.Equal(TimeSpan.FromMinutes(2), defaults.InitializeTimeout);
        Assert.Equal(TimeSpan.FromMinutes(1), defaults.RequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), defaults.CancelGracePeriod);
        Assert.Equal(20, defaults.StderrTailLines);
    }

    [Theory]
    [InlineData("RegistryUrl", "http://example.com/r.json")]
    [InlineData("InstallRoot", "relative")]
    [InlineData("RegistryMaxBytes", "0")]
    [InlineData("CancelGracePeriod", "00:00:00")]
    public void Validate_InvalidOption_RejectsConfiguration(string key, string value) =>
        Assert.Throws<OptionsValidationException>(() => ReadOptions([new($"AcpAgents:{key}", value)]));

    [Fact]
    public void Validate_LoopbackRegistryUrl_AcceptsConfiguration() =>
        Assert.Equal(
            "http://127.0.0.1:5000/registry.json",
            ReadOptions([new("AcpAgents:RegistryUrl", "http://127.0.0.1:5000/registry.json")]).RegistryUrl.AbsoluteUri
        );

    [Theory]
    [InlineData("https://x", true)]
    [InlineData("http://x", false)]
    [InlineData("http://localhost", true)]
    [InlineData("http://127.0.0.5", true)]
    [InlineData("http://[::1]", true)]
    [InlineData("file:///x", false)]
    public void IsAllowedDownloadUri_Schemes_MatchDesign(string uri, bool allowed) =>
        Assert.Equal(allowed, AcpRegistryRules.IsAllowedDownloadUri(new Uri(uri)));

    private static AcpAgentOptions ReadOptions(IEnumerable<KeyValuePair<string, string?>> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        using var services = new ServiceCollection().AddAcpAgentInstallation(configuration).BuildServiceProvider();
        return services.GetRequiredService<IOptions<AcpAgentOptions>>().Value;
    }
}
