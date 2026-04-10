using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

using Agw.Domain.Services;
using Agw.Infrastructure.Data;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Tasks;

using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Tasks.Tests;

public class EfCoreChatHistoryProviderTests
{
    [Fact]
    public async Task StoreChatHistoryAsync_PersistsTargetMetadataFromRequestMessage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;

        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
        }

        var projectId = Guid.NewGuid();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Projects.Add(new Project
            {
                Id = projectId,
                Name = "Chat Project",
                Type = ProjectType.UserDefined,
                Enable = true,
                CreateBy = "tester",
                CreateTime = DateTime.UtcNow
            });
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => new AgwDbContext(options));
        await using var serviceProvider = services.BuildServiceProvider();

        var provider = new EfCoreChatHistoryProvider(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfCoreChatHistoryProvider>.Instance);

        var session = new FakeAgentSession();
        var taskId = Guid.NewGuid();
        provider.InitializeSessionState(session, "context-1", taskId.ToString("D"), projectId);

        var message = new ChatMessage(
            ChatRole.User,
            [
                new TextContent("hello")
                {
                    AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        ["targetType"] = "agent",
                        ["targetId"] = "11111111-1111-1111-1111-111111111111"
                    }
                }
            ])
        {
            MessageId = Guid.NewGuid().ToString(),
            AuthorName = "tester"
        };

        var context = new ChatHistoryProvider.InvokedContext(
            new FakeAgent(),
            session,
            [message],
            []);

        await InvokeStoreChatHistoryAsync(provider, context, cancellationToken);

        await using var verifyContext = new AgwDbContext(options);
        var record = await verifyContext.TaskRecords.SingleAsync(
            x => x.TaskId == taskId,
            cancellationToken);

        Assert.NotNull(record.Metadata);
        Assert.Equal("agent", record.Metadata!["targetType"].GetString());
        Assert.Equal("11111111-1111-1111-1111-111111111111", record.Metadata["targetId"].GetString());
    }

    private static async Task InvokeStoreChatHistoryAsync(
        EfCoreChatHistoryProvider provider,
        ChatHistoryProvider.InvokedContext context,
        CancellationToken cancellationToken)
    {
        var method = typeof(EfCoreChatHistoryProvider).GetMethod(
            "StoreChatHistoryAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        var valueTask = (ValueTask)method!.Invoke(provider, [context, cancellationToken])!;
        await valueTask;
    }

    private sealed class FakeAgent : AIAgent
    {
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new FakeAgentSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new FakeAgentSession());

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield break;
        }
    }

    private sealed class FakeAgentSession : AgentSession;
}
