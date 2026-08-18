using Agw.Shared.Data.Entities.Agents;
using Agw.Testing;

namespace Agw.Agents.Tests;

public class McpToolServerDomainServiceTests
{
    private static readonly DateTimeOffset UtcNow = new(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
    private readonly McpToolServerDomainService _service = new(new TestTimeProvider(UtcNow));

    [Fact]
    public void PrepareForCreate_InitializesOptionalCollectionsAndCreateMetadata()
    {
        var server = new McpServer
        {
            Name = "stdio-server",
            Arguments = null!,
            EnvironmentVariables = null!,
            Headers = null!,
        };

        _service.PrepareForCreate(server, "tester");

        Assert.NotEqual(Guid.Empty, server.Id);
        Assert.Empty(server.Arguments);
        Assert.Empty(server.EnvironmentVariables);
        Assert.Empty(server.Headers);
        Assert.Equal("tester", server.CreateBy);
        Assert.Equal(UtcNow, server.CreateTime);
    }

    [Fact]
    public void ApplyUpdate_NormalizesCollectionsAndSetsUpdateMetadata()
    {
        var server = new McpServer
        {
            Id = Guid.CreateVersion7(),
            Name = "server",
            Arguments = ["--existing"],
            EnvironmentVariables = new Dictionary<string, string> { ["A"] = "1" },
            Headers = new Dictionary<string, string> { ["H"] = "v" },
        };
        _service.ApplyUpdate(
            server,
            current =>
            {
                current.Arguments = null!;
                current.EnvironmentVariables = null!;
                current.Headers = null!;
                current.Name = "updated";
            },
            "updater"
        );

        Assert.Equal("updated", server.Name);
        Assert.Empty(server.Arguments);
        Assert.Empty(server.EnvironmentVariables);
        Assert.Empty(server.Headers);
        Assert.Equal("updater", server.UpdateBy);
        Assert.Equal(UtcNow, server.UpdateTime);
    }

    [Fact]
    public void NormalizeAgentIds_RemovesEmptyValuesAndDuplicates()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        var result = _service.NormalizeAgentIds([Guid.Empty, first, first, second]);

        Assert.Equal([first, second], result);
    }
}
