using System.Runtime.InteropServices;
using System.Text.Json;
using Agw.Agents.Execution.Agents.ExternalAgents.Acp;
using Agw.Agents.ExternalAgents.Acp;
using Agw.Agents.Infrastructure.Acp;
using Agw.Shared.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agw.Agents.Tests.Acp;

public sealed class AcpLaunchResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "Acp", $"launch-{Guid.NewGuid():N}");

    [Fact]
    public void Resolve_Command_UsesArgs()
    {
        var spec = CreateResolver().Resolve(AcpAgentSettingsJson.Parse("""{"command":"opencode","args":["acp"]}"""));

        Assert.Equal("command", spec.Source);
        Assert.Equal("opencode", spec.FileName);
        Assert.Equal(["acp"], spec.Arguments);
        Assert.Empty(spec.Environment);
    }

    [Theory]
    [InlineData("node", "npx")]
    [InlineData("native", "binary")]
    public void Resolve_InstalledAgent_UsesManifest(string runtime, string distribution)
    {
        var version = Directory.CreateDirectory(Path.Combine(_root, "goose", "1.0.0")).FullName;
        const string entry = "agent-bin";
        var executable = Path.Combine(version, entry);
        File.WriteAllText(executable, "");
        var manifest = new AcpInstallManifest(
            "goose",
            "goose",
            "1.0.0",
            new("https://example.test/registry.json"),
            distribution,
            AcpPlatform.Current ?? RuntimeInformation.RuntimeIdentifier,
            runtime,
            entry,
            ["acp"],
            new Dictionary<string, string> { ["KEY"] = "value" },
            TimeProvider.System.GetUtcNow()
        );
        File.WriteAllText(
            Path.Combine(version, AcpInstallManifest.FileName),
            JsonSerializer.Serialize(manifest, JsonSerializerOptions.Web)
        );

        var spec = CreateResolver().Resolve(AcpAgentSettingsJson.Parse("""{"registry":{"agentId":"goose"}}"""));

        Assert.Equal(runtime == "node" ? "node" : executable, spec.FileName);
        Assert.Equal(runtime == "node" ? [executable, "acp"] : ["acp"], spec.Arguments);
        Assert.Equal("value", spec.Environment["KEY"]);
        Assert.Equal(distribution, spec.Source);
        Assert.Equal("1.0.0", spec.Version);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private AcpLaunchResolver CreateResolver() =>
        new(
            new AcpInstalledAgentCatalog(
                Options.Create(new AcpAgentOptions { InstallRoot = _root }),
                AgwDataPaths.Resolve(_root, _root),
                NullLogger<AcpInstalledAgentCatalog>.Instance
            )
        );
}
