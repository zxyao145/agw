namespace Agw.Agents.Tests;

public class McpToolServerDomainServiceTests
{
    private readonly McpToolServerDomainService _service = new();

    [Fact]
    public void PrepareForCreate_InitializesOptionalCollectionsAndCreateMetadata()
    {
        var before = DateTime.UtcNow;
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
        Assert.InRange(server.CreateTime, before, DateTime.UtcNow);
    }

    [Fact]
    public void ApplyUpdate_NormalizesCollectionsAndSetsUpdateMetadata()
    {
        var server = new McpServer
        {
            Id = Guid.NewGuid(),
            Name = "server",
            Arguments = ["--existing"],
            EnvironmentVariables = new Dictionary<string, string> { ["A"] = "1" },
            Headers = new Dictionary<string, string> { ["H"] = "v" },
        };
        var before = DateTime.UtcNow;

        _service.ApplyUpdate(
            server,
            current =>
            {
                current.Arguments = null!;
                current.EnvironmentVariables = null!;
                current.Headers = null!;
                current.Name = "updated";
            },
            "updater");

        Assert.Equal("updated", server.Name);
        Assert.Empty(server.Arguments);
        Assert.Empty(server.EnvironmentVariables);
        Assert.Empty(server.Headers);
        Assert.Equal("updater", server.UpdateBy);
        Assert.InRange(server.UpdateTime!.Value, before, DateTime.UtcNow);
    }

    [Fact]
    public void NormalizeAgentIds_RemovesEmptyValuesAndDuplicates()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var result = _service.NormalizeAgentIds([Guid.Empty, first, first, second]);

        Assert.Equal([first, second], result);
    }
}
