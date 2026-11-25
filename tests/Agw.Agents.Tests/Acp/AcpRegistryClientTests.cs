using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Agw.Agents.Application.Acp;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.ExternalAgents.Acp;
using Agw.Agents.Infrastructure.Acp;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Agw.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Agents.Tests.Acp;

[Collection("ACP registry")]
public sealed class AcpRegistryClientTests
{
    internal const string Registry = """
        {"version":"1.0.0","extensions":[],"agents":[{"id":"goose","name":"goose","version":"1.60.2",
        "description":"Local agent","authors":["Block"],"distribution":{"binary":{},"npx":{"package":"goose@1.60.2"}}}]}
        """;

    internal static ServiceProvider Services(
        Uri url,
        TimeProvider? clock = null,
        string? root = null,
        long limit = 8388608,
        TimeSpan? downloadTimeout = null
    )
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AcpAgents:RegistryUrl"] = url.AbsoluteUri,
                    ["AcpAgents:RegistryMaxBytes"] = limit.ToString(),
                    ["AcpAgents:MaxDownloadBytes"] = limit.ToString(),
                    ["AcpAgents:InstallRoot"] = root,
                    ["AcpAgents:DownloadTimeout"] = (downloadTimeout ?? TimeSpan.FromMinutes(10)).ToString("c"),
                }
            )
            .Build();
        return new ServiceCollection()
            .AddLogging()
            .AddSingleton(clock ?? TimeProvider.System)
            .AddSingleton(AgwDataPaths.Resolve(AppContext.BaseDirectory, AppContext.BaseDirectory))
            .AddAcpAgentInstallation(config)
            .BuildServiceProvider();
    }

    [Fact]
    public async Task GetAsync_FirstCall_ParsesDocumentIgnoringUnknownFields()
    {
        await using var server = await AcpTestRegistryServer.StartAsync();
        server.SetRegistry(Registry);
        using var provider = Services(server.RegistryUrl);
        var result = await provider
            .GetRequiredService<IAcpRegistryClient>()
            .GetAsync(false, TestContext.Current.CancellationToken);
        var agent = Assert.Single(result.Document.Agents);
        Assert.Equal("goose", agent.Id);
        Assert.Equal(["Block"], agent.Authors);
        Assert.Equal(server.RegistryUrl, result.SourceUrl);
    }

    [Fact]
    public async Task GetAsync_NotModified_UpdatesTimeAndRecordsOutcomes()
    {
        await using var server = await AcpTestRegistryServer.StartAsync();
        var outcomes = new ConcurrentQueue<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        {
            if (instrument.Name == "agw.acp.client.registry_fetches")
                meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>(
            (_, _, tags, _) =>
            {
                foreach (var tag in tags)
                    if (tag.Key == "outcome")
                        outcomes.Enqueue((string)tag.Value!);
            }
        );
        listener.Start();
        var clock = new TestTimeProvider(DateTimeOffset.UnixEpoch);
        using var provider = Services(server.RegistryUrl, clock);
        var client = provider.GetRequiredService<IAcpRegistryClient>();
        var token = TestContext.Current.CancellationToken;
        var first = await client.GetAsync(false, token);
        clock.SetUtcNow(first.FetchedAt.AddMinutes(5));
        var next = await client.GetAsync(true, token);
        Assert.Same(first.Document, next.Document);
        Assert.Equal(clock.GetUtcNow(), next.FetchedAt);
        Assert.Contains("fetched", outcomes);
        Assert.Contains("not_modified", outcomes);
    }

    [Fact]
    public async Task GetAsync_ServerStopped_UsesMemorySnapshot()
    {
        await using var server = await AcpTestRegistryServer.StartAsync();
        using var provider = Services(server.RegistryUrl);
        var client = provider.GetRequiredService<IAcpRegistryClient>();
        var token = TestContext.Current.CancellationToken;
        var first = await client.GetAsync(false, token);
        await server.StopAsync();
        Assert.Same(first, await client.GetAsync(true, token));
        Assert.Equal(
            ErrorCodes.AcpRegistryUnavailable.Code,
            (await Assert.ThrowsAsync<AgwException>(() => client.FetchAsync(token))).Code
        );
    }

    [Fact]
    public async Task GetAsync_RegistryLargerThanLimit_ReturnsUnavailable()
    {
        await using var server = await AcpTestRegistryServer.StartAsync();
        server.SetRegistry(Registry + new string(' ', 1024));
        using var provider = Services(server.RegistryUrl, limit: 1024);
        var client = provider.GetRequiredService<IAcpRegistryClient>();
        Assert.Equal(
            ErrorCodes.AcpRegistryUnavailable.Code,
            (
                await Assert.ThrowsAsync<AgwException>(() =>
                    client.GetAsync(false, TestContext.Current.CancellationToken)
                )
            ).Code
        );
    }

    [Fact]
    public async Task GetAsync_ConcurrentCalls_SendOneRequest()
    {
        await using var server = await AcpTestRegistryServer.StartAsync();
        using var provider = Services(server.RegistryUrl);
        var client = provider.GetRequiredService<IAcpRegistryClient>();
        await Task.WhenAll(
            Enumerable.Range(0, 10).Select(_ => client.GetAsync(true, TestContext.Current.CancellationToken))
        );
        Assert.Equal(1, server.RegistryRequestCount);
    }

    [Fact]
    public async Task SendAsync_RedirectToDisallowedUri_Fails()
    {
        await using var server = await AcpTestRegistryServer.StartAsync();
        using var provider = Services(server.RegistryUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(server.RegistryUrl, "/redirect"));
        await Assert.ThrowsAsync<AgwException>(() =>
            provider.GetRequiredService<AcpDownloadClient>().SendAsync(request, TestContext.Current.CancellationToken)
        );
    }
}

[Collection("ACP registry")]
public sealed class AcpRegistryAppServiceTests
{
    [Fact]
    public async Task GetAsync_Distributions_ListedInOrder()
    {
        await using var server = await AcpTestRegistryServer.StartAsync();
        server.SetRegistry(AcpRegistryClientTests.Registry);
        using var provider = AcpRegistryClientTests.Services(server.RegistryUrl);
        var service = new AcpRegistryAppService(provider.GetRequiredService<IAcpRegistryClient>());
        var result = await service.GetAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(["binary", "npx"], Assert.Single(result.Agents).Distributions);
    }
}
