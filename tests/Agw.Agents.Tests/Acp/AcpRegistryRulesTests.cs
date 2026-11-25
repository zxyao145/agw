using Agw.Agents.Definitions.Domain.Rules;
using Agw.Agents.ExternalAgents.Acp;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Tests.Acp;

public sealed class AcpRegistryRulesTests
{
    [Theory]
    [InlineData("./goose-package\\goose.exe", "goose-package/goose.exe")]
    [InlineData("amp-acp.exe", "amp-acp.exe")]
    public void NormalizeCmd_RelativeCommand_ReturnsRelativePath(string command, string expected) =>
        Assert.Equal(expected, AcpRegistryRules.NormalizeCmd(command));

    [Theory]
    [InlineData("/bin/x")]
    [InlineData("C:\\x.exe")]
    [InlineData("./a/../../b")]
    public void NormalizeCmd_OutsideRoot_RejectsPath(string command) =>
        Assert.Equal(
            ErrorCodes.InvalidParam.Code,
            Assert.Throws<AgwException>(() => AcpRegistryRules.NormalizeCmd(command)).Code
        );

    [Theory]
    [InlineData("goose", "linux-x86_64", "./goose", "abc", false, "binary")]
    [InlineData("goose", "linux-x86_64", "./goose", null, false, "npx")]
    [InlineData("goose", "linux-x86_64-musl", "./goose", "abc", false, "npx")]
    [InlineData("goose", "windows-x86_64", "./goose.cmd", "abc", true, "npx")]
    public void SelectDistribution_AvailableTargets_PrefersUsableKind(
        string id,
        string platform,
        string cmd,
        string? sha,
        bool windows,
        string kind
    )
    {
        var target = new AcpBinaryTarget(new("https://example.com/goose.tar.bz2"), cmd, ["acp"], null, sha);
        var platforms = new Dictionary<string, AcpBinaryTarget> { [windows ? platform : "linux-x86_64"] = target };
        var entry = new AcpRegistryAgent(
            id,
            id,
            "1.60.2",
            "",
            null,
            null,
            null,
            new(platforms, new("@agentclientprotocol/codex-acp@2.10.0", null, null), null)
        );
        Assert.Equal(kind, AcpRegistryRules.SelectDistribution(entry, new(platform, true, true, windows), false).Kind);
    }

    [Fact]
    public void SelectDistribution_NpmMissing_ReportsReasons()
    {
        var entry = new AcpRegistryAgent(
            "codex-acp",
            "Codex",
            "2.10.0",
            "",
            null,
            null,
            null,
            new(null, new("@agentclientprotocol/codex-acp@2.10.0", null, null), null)
        );
        var selection = AcpRegistryRules.SelectDistribution(entry, new("linux-x86_64", false, true, false), false);
        Assert.Null(selection.Kind);
        Assert.Equal(
            ["binary: no target for linux-x86_64", "npx: npm was not found", "uvx: not provided"],
            selection.Reasons
        );
    }
}
