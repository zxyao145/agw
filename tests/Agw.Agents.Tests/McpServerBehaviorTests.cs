using Agw.Agents.Definitions.Domain.Behaviors;
using Agw.Shared.Data.Entities.Agents;

namespace Agw.Agents.Tests;

public class McpServerBehaviorTests
{
    [Fact]
    public void NormalizeCollections_NullCollections_InitializesWithoutAuditStamping()
    {
        var server = new McpServer
        {
            Arguments = null!,
            EnvironmentVariables = null!,
            Headers = null!,
        };

        new McpServerBehavior(server).NormalizeCollections();

        Assert.Empty(server.Arguments);
        Assert.Empty(server.EnvironmentVariables);
        Assert.Empty(server.Headers);
        Assert.Equal(Guid.Empty, server.Id);
        Assert.Null(server.CreateBy);
        Assert.Equal(default, server.CreateTime);
        Assert.Null(server.UpdateBy);
        Assert.Null(server.UpdateTime);
    }

    [Fact]
    public void NormalizeCollections_ExistingCollections_PreservesInstancesAndValues()
    {
        var arguments = new List<string> { "--existing" };
        var environment = new Dictionary<string, string> { ["A"] = "1" };
        var headers = new Dictionary<string, string> { ["H"] = "v" };
        var server = new McpServer
        {
            Arguments = arguments,
            EnvironmentVariables = environment,
            Headers = headers,
        };

        new McpServerBehavior(server).NormalizeCollections();

        Assert.Same(arguments, server.Arguments);
        Assert.Same(environment, server.EnvironmentVariables);
        Assert.Same(headers, server.Headers);
        Assert.Equal("--existing", Assert.Single(server.Arguments));
        Assert.Equal("1", server.EnvironmentVariables["A"]);
        Assert.Equal("v", server.Headers["H"]);
    }
}
