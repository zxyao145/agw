using System.Runtime.InteropServices;
using Agw.Agents.ExternalAgents.Acp;

namespace Agw.Agents.Tests.Acp;

public sealed class AcpPlatformTests
{
    [Theory]
    [InlineData("OSX", Architecture.Arm64, "osx-arm64", "darwin-aarch64")]
    [InlineData("LINUX", Architecture.X64, "linux-musl-x64", "linux-x86_64-musl")]
    [InlineData("WINDOWS", Architecture.X64, "win-x64", "windows-x86_64")]
    [InlineData("LINUX", Architecture.S390x, "linux-s390x", null)]
    public void Resolve_KnownPlatforms_ReturnsKeys(string os, Architecture arch, string rid, string? expected) =>
        Assert.Equal(expected, AcpPlatform.Resolve(OSPlatform.Create(os), arch, rid));

    [Fact]
    public void Current_Platform_MatchesRuntime()
    {
        var expected = RuntimeInformation.RuntimeIdentifier switch
        {
            "osx-arm64" => "darwin-aarch64",
            "osx-x64" => "darwin-x86_64",
            "linux-arm64" => "linux-aarch64",
            "linux-x64" => "linux-x86_64",
            "linux-musl-arm64" => "linux-aarch64-musl",
            "linux-musl-x64" => "linux-x86_64-musl",
            "win-arm64" => "windows-aarch64",
            "win-x64" => "windows-x86_64",
            var other => other,
        };
        Assert.Equal(expected, AcpPlatform.InstallationPlatform);
    }
}
