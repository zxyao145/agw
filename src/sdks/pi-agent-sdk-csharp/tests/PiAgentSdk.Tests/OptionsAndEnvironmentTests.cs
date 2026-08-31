using System.Collections;
using PiAgentSdk.Internal;
using Xunit;

namespace PiAgentSdk.Tests;

public sealed class OptionsAndEnvironmentTests
{
    [Fact]
    public void Build_DefaultOptions_UsesRpcAndNoApprove()
    {
        // Arrange
        var options = new PiSessionOptions();

        // Act
        var arguments = PiProcessArguments.Build(options, resumeSessionId: null);

        // Assert
        Assert.Equal(["--mode", "rpc"], arguments.Take(2));
        Assert.Contains("--no-approve", arguments);
        Assert.DoesNotContain("--session", arguments);
    }

    [Fact]
    public void Build_WithSessionDirAndResume_EmitsBothFlags()
    {
        // Arrange
        var options = new PiSessionOptions { SessionDir = "/safe/sessions" };

        // Act
        var arguments = PiProcessArguments.Build(options, "session-1");

        // Assert
        Assert.Contains("--session-dir", arguments);
        Assert.Equal("/safe/sessions", arguments[arguments.IndexOf("--session-dir") + 1]);
        Assert.Equal("session-1", arguments[arguments.IndexOf("--session") + 1]);
    }

    [Fact]
    public void Build_NoExtensions_EmitsNoExtensionsFlag()
    {
        // Arrange
        var options = new PiSessionOptions { NoExtensions = true };

        // Act
        var arguments = PiProcessArguments.Build(options, resumeSessionId: null);

        // Assert
        Assert.Contains("--no-extensions", arguments);
    }

    [Fact]
    public void Build_NoDiscoveryWithExplicitExtensions_EmitsTrustedExtensionPaths()
    {
        // Arrange
        var options = new PiSessionOptions
        {
            NoExtensions = true,
            Extensions = ["/trusted/first.ts", "/trusted/second.ts"],
        };

        // Act
        var arguments = PiProcessArguments.Build(options, resumeSessionId: null);

        // Assert
        Assert.Contains("--no-extensions", arguments);
        Assert.Equal(2, arguments.Count(argument => argument == "--extension"));
        Assert.Contains("/trusted/first.ts", arguments);
        Assert.Contains("/trusted/second.ts", arguments);
    }

    [Fact]
    public void BuildEnvironment_PreservesProxyCertificatesAndLocaleButNotHostSecrets()
    {
        // Arrange
        IDictionary host = new Hashtable
        {
            ["PATH"] = "/bin",
            ["LC_CTYPE"] = "en_US.UTF-8",
            ["HTTPS_PROXY"] = "http://proxy",
            ["NODE_EXTRA_CA_CERTS"] = "/certs/root.pem",
            ["NODE_OPTIONS"] = "--require malware.js",
            ["ANTHROPIC_API_KEY"] = "host-secret",
        };
        var agent = new PiAgentOptions
        {
            EnvironmentVariables = new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "explicit" },
        };

        // Act
        var environment = PiProcessEnvironment.Build(agent, new PiSessionOptions(), host);

        // Assert
        Assert.Equal("/bin", environment["PATH"]);
        Assert.Equal("en_US.UTF-8", environment["LC_CTYPE"]);
        Assert.Equal("http://proxy", environment["HTTPS_PROXY"]);
        Assert.Equal("/certs/root.pem", environment["NODE_EXTRA_CA_CERTS"]);
        Assert.Equal("explicit", environment["ANTHROPIC_API_KEY"]);
        Assert.DoesNotContain("NODE_OPTIONS", environment.Keys);
    }

    [Fact]
    public void Constructor_NonPositiveTimeout_Throws()
    {
        // Arrange
        var options = new PiAgentOptions { CommandTimeout = TimeSpan.Zero };

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new PiAgent(options));
    }
}

internal static class ListExtensions
{
    public static int IndexOf(this IReadOnlyList<string> values, string value)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index] == value)
            {
                return index;
            }
        }

        return -1;
    }
}
