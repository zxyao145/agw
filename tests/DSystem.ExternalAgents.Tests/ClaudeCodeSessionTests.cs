using DSystem.ExternalAgents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DSystem.ExternalAgents.Tests;

public class ClaudeCodeSessionTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteStreamingAsync_WhenInputIsWhitespace_ThrowsArgumentException(string input)
    {
        var session = CreateSession();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await using var enumerator = session.ExecuteStreamingAsync(input!).GetAsyncEnumerator();
            await enumerator.MoveNextAsync();
        });
    }

    [Fact]
    public async Task ExecuteStreamingAsync_WhenInputIsNull_ThrowsArgumentNullException()
    {
        var session = CreateSession();

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        {
            await using var enumerator = session.ExecuteStreamingAsync((string)null!).GetAsyncEnumerator();
            await enumerator.MoveNextAsync();
        });
    }

    [Fact]
    public void CancelActiveRequest_SetsCancellationToken()
    {
        var session = CreateSession();

        Assert.False(session.CancellationToken.IsCancellationRequested);

        session.CancelActiveRequest();

        Assert.True(session.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void ResetCancellationToken_CreatesFreshToken()
    {
        var session = CreateSession();

        session.CancelActiveRequest();
        var canceledToken = session.CancellationToken;

        session.ResetCancellationToken();

        Assert.NotEqual(canceledToken, session.CancellationToken);
        Assert.False(session.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void UpdateThread_ReplacesThreadInstance()
    {
        var session = CreateSession();
        var originalThread = session.Thread;
        var newThread = new TestAgentThread();

        session.UpdateThread(newThread);

        Assert.Same(newThread, session.Thread);
        Assert.NotSame(originalThread, session.Thread);
    }

    [Fact]
    public void Constructor_NullAgent_ThrowsArgumentNullException()
    {
        var thread = new TestAgentThread();
        var configuration = new ClaudeCodeSettingRequest { SessionId = "session" };
        var logger = NullLogger.Instance;
        var cache = CreateCache();

        Assert.Throws<ArgumentNullException>(() => new ClaudeCodeSession(null!, thread, configuration, logger, cache));
    }

    [Fact]
    public void Constructor_NullThread_ThrowsArgumentNullException()
    {
        var agent = new TestClaudeCodeAIAgent();
        var configuration = new ClaudeCodeSettingRequest { SessionId = "session" };
        var logger = NullLogger.Instance;
        var cache = CreateCache();

        Assert.Throws<ArgumentNullException>(() => new ClaudeCodeSession(agent, null!, configuration, logger, cache));
    }

    [Fact]
    public void Constructor_NullConfiguration_ThrowsArgumentNullException()
    {
        var agent = new TestClaudeCodeAIAgent();
        var thread = new TestAgentThread();
        var logger = NullLogger.Instance;
        var cache = CreateCache();

        Assert.Throws<ArgumentNullException>(() => new ClaudeCodeSession(agent, thread, null!, logger, cache));
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var agent = new TestClaudeCodeAIAgent();
        var thread = new TestAgentThread();
        var configuration = new ClaudeCodeSettingRequest { SessionId = "session" };
        var cache = CreateCache();

        Assert.Throws<ArgumentNullException>(() => new ClaudeCodeSession(agent, thread, configuration, null!, cache));
    }

    [Fact]
    public void Constructor_NullCache_ThrowsArgumentNullException()
    {
        var agent = new TestClaudeCodeAIAgent();
        var thread = new TestAgentThread();
        var configuration = new ClaudeCodeSettingRequest { SessionId = "session" };
        var logger = NullLogger.Instance;

        Assert.Throws<ArgumentNullException>(() => new ClaudeCodeSession(agent, thread, configuration, logger, null!));
    }

    private static ClaudeCodeSession CreateSession()
    {
        var agent = new TestClaudeCodeAIAgent();
        var thread = new TestAgentThread();
        var configuration = new ClaudeCodeSettingRequest { SessionId = "session" };
        var logger = NullLogger.Instance;
        var cache = CreateCache();

        return new ClaudeCodeSession(agent, thread, configuration, logger, cache);
    }

    private static HybridCache CreateCache()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<HybridCache>();
    }
}
