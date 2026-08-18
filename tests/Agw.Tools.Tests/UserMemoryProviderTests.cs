using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Auth.Application;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Data.Encryption;
using Agw.Infrastructure.Repositories;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Entities.Tools;
using Agw.Shared.Data.Repositories;
using Agw.Tools.Application;
using Agw.Tools.ToolBlocks.Blocks.UserMemory;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Tools.Tests;

public sealed class UserMemoryProviderTests
{
    [Fact]
    public async Task Tools_CrudCurrentUserAndContextUsesContentNotDescription()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.CreateAsync(cancellationToken);
        var provider = new UserMemoryProvider(fixture.ScopeFactory);
        var context = await InvokeProviderAsync(provider);

        Assert.Equal(
            [
                UserMemoryProvider.DeleteToolName,
                UserMemoryProvider.ListToolName,
                UserMemoryProvider.ReadToolName,
                UserMemoryProvider.WriteToolName,
            ],
            context.Tools!.Select(tool => tool.Name).Order(StringComparer.Ordinal)
        );
        Assert.Null(context.Messages);

        var write = GetFunction(context, UserMemoryProvider.WriteToolName);
        Assert.Equal(
            "User memory 'Profile' written.",
            ResultText(
                await write.InvokeAsync(
                    Arguments(
                        ("name", "Profile"),
                        ("content", "Secret full content"),
                        ("description", "Answer preferences")
                    ),
                    cancellationToken
                )
            )
        );

        var refreshed = await InvokeProviderAsync(provider);
        var memoryContext = Assert.Single(refreshed.Messages!).Text;
        Assert.Contains("Profile", memoryContext);
        Assert.Contains("Secret full content", memoryContext);
        Assert.DoesNotContain("Answer preferences", memoryContext);

        var read = GetFunction(refreshed, UserMemoryProvider.ReadToolName);
        Assert.Equal(
            "Secret full content",
            ResultText(await read.InvokeAsync(Arguments(("name", "profile")), cancellationToken))
        );
        var list = ResultList<UserMemoryToolListItem>(
            await GetFunction(refreshed, UserMemoryProvider.ListToolName)
                .InvokeAsync(new AIFunctionArguments(), cancellationToken)
        );
        var listItem = Assert.Single(list);
        Assert.Equal("Profile", listItem.Name);
        Assert.Equal("Answer preferences", listItem.Description);

        await using (var scope = fixture.ScopeFactory.CreateAsyncScope())
        {
            fixture.SetUserId("user-b");
            var appService = scope.ServiceProvider.GetRequiredService<UserMemoryAppService>();
            Assert.Null(await appService.GetByNameAsync("Profile", cancellationToken));
        }
        fixture.SetUserId("user-a");

        Assert.Equal(
            "User memory 'PROFILE' deleted.",
            ResultText(
                await GetFunction(refreshed, UserMemoryProvider.DeleteToolName)
                    .InvokeAsync(Arguments(("name", "PROFILE")), cancellationToken)
            )
        );
    }

    [Fact]
    public async Task ToolBlock_AllowsOnlyListAndReadInPlanMode()
    {
        await using var fixture = await Fixture.CreateAsync(TestContext.Current.CancellationToken);
        var block = new UserMemoryToolBlock(fixture.ScopeFactory);
        var contribution = await block.MaterializeAsync(
            new UserMemoryToolBlockDefinition(),
            new ToolMaterializationContext
            {
                Agent = new Agent { Id = Guid.CreateVersion7() },
                Project = new Project { Id = Guid.CreateVersion7() },
                Workspace = string.Empty,
                DefaultMode = "plan",
            },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(ToolBlockScope.Agent | ToolBlockScope.Project, block.Descriptor.Scopes);
        Assert.Equal(
            [UserMemoryProvider.ListToolName, UserMemoryProvider.ReadToolName],
            contribution.PlanModeAllowedToolNames.Order(StringComparer.Ordinal)
        );
        Assert.DoesNotContain(UserMemoryProvider.WriteToolName, contribution.PlanModeAllowedToolNames);
        Assert.DoesNotContain(UserMemoryProvider.DeleteToolName, contribution.PlanModeAllowedToolNames);
        Assert.Single(contribution.ContextProviders);
    }

    [Fact]
    public async Task Context_InjectsAtMostFiftyMemoryContents()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.CreateAsync(cancellationToken);
        await using (var scope = fixture.ScopeFactory.CreateAsyncScope())
        {
            var appService = scope.ServiceProvider.GetRequiredService<UserMemoryAppService>();
            for (var index = 0; index < 51; index++)
            {
                await appService.CreateAsync(
                    $"Memory {index:D2}",
                    $"Summary {index:D2}",
                    $"Content {index:D2}",
                    cancellationToken
                );
            }
        }

        var context = await InvokeProviderAsync(new UserMemoryProvider(fixture.ScopeFactory));
        var contextMessage = Assert.Single(context.Messages!).Text;

        Assert.Equal(50, contextMessage.Split("## Memory ", StringSplitOptions.None).Length - 1);
        Assert.Contains("Memory 49", contextMessage);
        Assert.Contains("Content 00", contextMessage);
        Assert.Contains("Content 49", contextMessage);
        Assert.DoesNotContain("Memory 50", contextMessage);
        Assert.DoesNotContain("Content 50", contextMessage);
    }

    private static async Task<AIContext> InvokeProviderAsync(UserMemoryProvider provider)
    {
        var agent = new ChatClientAgent(new StubChatClient(), new ChatClientAgentOptions { Name = "test-agent" });
        return await provider.InvokingAsync(
            new AIContextProvider.InvokingContext(agent, null, new AIContext()),
            TestContext.Current.CancellationToken
        );
    }

    private static AIFunction GetFunction(AIContext context, string name) =>
        Assert.IsAssignableFrom<AIFunction>(Assert.Single(context.Tools!, tool => tool.Name == name));

    private static AIFunctionArguments Arguments(params (string Name, object? Value)[] values) =>
        new(values.ToDictionary(value => value.Name, value => value.Value));

    private static string? ResultText(object? result) =>
        result is JsonElement element ? element.GetString() : Assert.IsType<string>(result);

    private static List<T> ResultList<T>(object? result) =>
        Assert.IsType<JsonElement>(result).Deserialize<List<T>>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private sealed class StubChatClient : IChatClient
    {
        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "done")]));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            SqliteConnection connection,
            ServiceProvider serviceProvider,
            UserMemoryAppServiceTests.TestUserInfoService userInfoService
        )
        {
            Connection = connection;
            ServiceProvider = serviceProvider;
            ScopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
            UserInfoService = userInfoService;
        }

        public SqliteConnection Connection { get; }

        public ServiceProvider ServiceProvider { get; }

        public IServiceScopeFactory ScopeFactory { get; }

        private UserMemoryAppServiceTests.TestUserInfoService UserInfoService { get; }

        public void SetUserId(string userId) => UserInfoService.SetUserId(userId);

        public static async Task<Fixture> CreateAsync(CancellationToken cancellationToken)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(cancellationToken);
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .UseSnakeCaseNamingConvention()
                .Options;
            var protector = new DataProtectionEncryptedDataProtector(new EphemeralDataProtectionProvider());
            var services = new ServiceCollection();
            services.AddScoped(_ => new AgwDbContext(options, protector));
            services.AddScoped<DbContext>(provider => provider.GetRequiredService<AgwDbContext>());
            services.AddScoped(typeof(IRepository<>), typeof(EfRepository<>));
            services.AddScoped<IUnitOfWork>(provider => provider.GetRequiredService<AgwDbContext>());
            services.AddSingleton<IApplicationLock, InMemoryApplicationLock>();
            var userInfoService = new UserMemoryAppServiceTests.TestUserInfoService("user-a");
            services.AddSingleton<IUserInfoService>(userInfoService);
            services.AddScoped<UserMemoryAppService>();
            var serviceProvider = services.BuildServiceProvider();
            await using (var scope = serviceProvider.CreateAsyncScope())
            {
                await scope
                    .ServiceProvider.GetRequiredService<AgwDbContext>()
                    .Database.EnsureCreatedAsync(cancellationToken);
            }
            return new Fixture(connection, serviceProvider, userInfoService);
        }

        public async ValueTask DisposeAsync()
        {
            await ServiceProvider.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
