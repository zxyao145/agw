using System.Text.Json;
using Agw.Agents.Application.Acp;
using Agw.Agents.ExternalAgents.Acp;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Agents.Tests.Acp;

public sealed class AcpInstalledAgentCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "Acp", Guid.NewGuid().ToString());
    private readonly ServiceProvider _services;

    public AcpInstalledAgentCatalogTests() =>
        _services = AcpRegistryClientTests.Services(new Uri("https://example.com"), root: _root);

    [Fact]
    public void Find_NoVersion_ReturnsHighestSemver()
    {
        WriteManifest("1.9.0", AcpPlatform.Current!, true);
        WriteManifest("1.10.0", AcpPlatform.Current!, true);
        var catalog = _services.GetRequiredService<IAcpInstalledAgentCatalog>();
        Assert.Equal("1.10.0", catalog.Find("goose", null).Manifest.Version);
        Assert.Equal("1.9.0", catalog.Find("goose", "1.9.0").Manifest.Version);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Find_IncompleteOrPlatformMismatch_SkipsVersion(bool matches, bool executable)
    {
        WriteManifest("1.0.0", matches ? AcpPlatform.Current! : "foreign-platform", executable);
        var error = Assert.Throws<AgwException>(() =>
            _services.GetRequiredService<IAcpInstalledAgentCatalog>().Find("goose", null)
        );
        Assert.Equal(ErrorCodes.AcpAgentNotInstalled.Code, error.Code);
        Assert.Contains("acp install goose", error.Message);
    }

    private void WriteManifest(string version, string platform, bool executable)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "goose", version)).FullName;
        var manifest = new AcpInstallManifest(
            "goose",
            "Goose",
            version,
            new("https://example.com"),
            "binary",
            platform,
            "native",
            "goose",
            [],
            new Dictionary<string, string>(),
            DateTimeOffset.UnixEpoch
        );
        File.WriteAllText(
            Path.Combine(directory, AcpInstallManifest.FileName),
            JsonSerializer.Serialize(manifest, JsonSerializerOptions.Web)
        );
        if (executable)
            File.WriteAllText(Path.Combine(directory, "goose"), "");
    }

    public void Dispose()
    {
        _services.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }
}
