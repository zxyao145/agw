using DSystem.Appliaction;
using DSystem.Appliaction.ExternalAgents;
using DSystem.Infrastructure.Data;
using DSystem.Infrastructure.Repositories;
using DSystem.SessionRecords.Application;
using DSystem.SessionRecords.Entities;
using DSystem.SessionRecords.Repositories;
using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
        var originalThread = session.Session;
        var newThread = new TestAgentThread();

        session.UpdateThread(newThread);

        Assert.Same(newThread, session.Session);
        Assert.NotSame(originalThread, session.Session);
    }

    [Fact]
    public void Constructor_NullAgent_ThrowsArgumentNullException()
    {
        var thread = new TestAgentThread();
        var configuration = new ClaudeCodeSettingRequest { SessionId = "session" };
        var logger = NullLogger.Instance;
        var deps = CreateDependencies();

        Assert.Throws<ArgumentNullException>(() => new AiAgentSession(null!, thread, configuration.ProjectId, configuration.SessionId, logger, deps.SessionRecordApplication));
    }

    [Fact]
    public void Constructor_NullThread_ThrowsArgumentNullException()
    {
        var agent = new TestClaudeCodeAIAgent();
        var configuration = new ClaudeCodeSettingRequest { SessionId = "session" };
        var logger = NullLogger.Instance;
        var deps = CreateDependencies();

        Assert.Throws<ArgumentNullException>(() => new AiAgentSession(agent, null!, configuration.ProjectId, configuration.SessionId, logger, deps.SessionRecordApplication));
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var agent = new TestClaudeCodeAIAgent();
        var thread = new TestAgentThread();
        var configuration = new ClaudeCodeSettingRequest { SessionId = "session" };
        var deps = CreateDependencies();

        Assert.Throws<ArgumentNullException>(() => new AiAgentSession(agent, thread, configuration.ProjectId, configuration.SessionId, null!, deps.SessionRecordApplication));
    }

    [Fact]
    public void Constructor_NullSessionRecordApplication_ThrowsArgumentNullException()
    {
        var agent = new TestClaudeCodeAIAgent();
        var thread = new TestAgentThread();
        var configuration = new ClaudeCodeSettingRequest { SessionId = "session" };
        var logger = NullLogger.Instance;
        var deps = CreateDependencies();

        Assert.Throws<ArgumentNullException>(() => new AiAgentSession(agent, thread, configuration.ProjectId, configuration.SessionId, logger, null!));
    }

    private static AiAgentSession CreateSession()
    {
        var agent = new TestClaudeCodeAIAgent();
        var thread = new TestAgentThread();
        var configuration = new ClaudeCodeSettingRequest { SessionId = "session" };
        var logger = NullLogger.Instance;
        var deps = CreateDependencies();

        return new AiAgentSession(agent, thread, configuration.ProjectId, configuration.SessionId, logger, deps.SessionRecordApplication);
    }

    private static (IAgentSessionRecordRepository Repository, ISessionRecordsUnitOfWork UnitOfWork, SessionRecordApplication SessionRecordApplication) CreateDependencies()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<LlmDbContext>()
            .UseSqlite(connection)
            .Options;

        var context = new LlmDbContext(options);
        context.Database.EnsureCreated();
        IAgentSessionRecordRepository repo = new AgentSessionRecordRepository(context);
        ISessionRecordsUnitOfWork unitOfWork = new SessionRecordsUnitOfWork(context);
        var application = new SessionRecordApplication(repo, unitOfWork);
        return (repo, unitOfWork, application);
    }
}
