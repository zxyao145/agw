using System.Collections;
using Agw.Agents.Execution.Agents.ExternalAgents.Acp;

namespace Agw.Agents.Tests.Acp;

public sealed class AcpProcessEnvironmentTests
{
    [Fact]
    public void Build_Environment_LaterSourcesOverride()
    {
        var actual = AcpProcessEnvironment.Build(
            new Hashtable
            {
                ["PATH"] = "/a",
                ["SECRET"] = "x",
                ["LC_ALL"] = "C",
            },
            new Dictionary<string, string> { ["PATH"] = "/b" },
            new Dictionary<string, string> { ["PATH"] = "/c", ["K"] = "v" }
        );
        Assert.Equal("/c", actual["PATH"]);
        Assert.Equal("v", actual["K"]);
        Assert.Equal("C", actual["LC_ALL"]);
        Assert.False(actual.ContainsKey("SECRET"));
    }
}
