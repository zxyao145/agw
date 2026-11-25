using System.Text.Json;
using Agw.Agents.ExternalAgents.Acp;
using Agw.Shared.Runtime;
using Microsoft.Extensions.Configuration;

namespace Agw.Agents.Tests.Acp;

[CollectionDefinition("ACP command", DisableParallelization = true)]
public sealed class AcpCommandCollection;

[Collection("ACP command")]
public sealed class AcpCommandTests
{
    [Theory]
    [InlineData("goose", true, 0)]
    [InlineData("missing-agent", true, 1)]
    [InlineData("goose", false, 1)]
    public async Task TryRunAsync_InstallWithoutDatabase_ReportsResultAndDiagnostics(
        string agentId,
        bool releaseExists,
        int expectedExitCode
    )
    {
        await using var server = await AcpTestRegistryServer.StartAsync();
        byte[] bytes = [42];
        var release = releaseExists ? server.AddFile("/goose", bytes) : new Uri(server.RegistryUrl, "/goose");
        var entry = AcpAgentInstallerTests.Binary(release, bytes, "goose");
        server.SetRegistry(
            JsonSerializer.Serialize(new AcpRegistryDocument("1.0.0", [entry]), JsonSerializerOptions.Web)
        );
        var (exitCode, output, diagnostics) = await RunInstallAsync(server.RegistryUrl, agentId);

        Assert.Equal(expectedExitCode, exitCode);
        if (expectedExitCode == 0)
        {
            const string resultPrefix = "installed goose 1.60.2 (binary) at ";
            Assert.StartsWith(resultPrefix, output);
            Assert.Single(output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
            Assert.Contains($"Downloaded {bytes.Length} ACP binary bytes, SHA-256 verified: True", diagnostics);
            Assert.Contains(
                $"Installed ACP agent goose 1.60.2 for {AcpPlatform.InstallationPlatform} from binary",
                diagnostics
            );
            Assert.Contains($"at {output[resultPrefix.Length..].TrimEnd()} in ", diagnostics);
        }
        else
        {
            Assert.Empty(output);
            Assert.Contains(releaseExists ? agentId : "Could not download ACP release", diagnostics);
        }
    }

    private static async Task<(int ExitCode, string Output, string Diagnostics)> RunInstallAsync(
        Uri registryUrl,
        string agentId
    )
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Acp", Guid.NewGuid().ToString());
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AcpAgents:RegistryUrl"] = registryUrl.AbsoluteUri,
                    ["Database:ConnectionString"] = "Host=unreachable.invalid;Database=absent",
                }
            )
            .Build();
        var previousCode = Environment.ExitCode;
        var stdout = Console.Out;
        var stderr = Console.Error;
        using var output = new StringWriter();
        using var diagnostics = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(diagnostics);
            Assert.True(
                await AcpCommand.TryRunAsync(["acp", "install", agentId], config, AgwDataPaths.Resolve(root, root))
            );
            if (Environment.ExitCode == 0)
                Assert.True(
                    File.Exists(Path.Combine(root, "acp-agents", "goose", "1.60.2", AcpInstallManifest.FileName))
                );
            return (Environment.ExitCode, output.ToString(), diagnostics.ToString());
        }
        finally
        {
            Environment.ExitCode = previousCode;
            Console.SetOut(stdout);
            Console.SetError(stderr);
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task TryRunAsync_OtherArgs_ReturnsFalse() =>
        Assert.False(
            await AcpCommand.TryRunAsync(
                ["serve"],
                new ConfigurationBuilder().Build(),
                AgwDataPaths.Resolve(AppContext.BaseDirectory, AppContext.BaseDirectory)
            )
        );
}
