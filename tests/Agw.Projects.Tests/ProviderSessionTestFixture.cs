using System.Security.Claims;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Data.Entities.Projects;
using Agw.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Projects.Tests;

/// <summary>
/// 共享内存 SQLite 上的 provider session 测试环境：外层 DbContext 代表执行连接所在 scope，协调器每次操作使用独立 scope。
/// A provider session test environment on shared in-memory SQLite: the outer DbContext stands for the execution connection's scope, and every coordinator operation uses its own scope.
/// </summary>
internal sealed class ProviderSessionTestFixture : IAsyncDisposable
{
    public const string Owner = "tester";
    public const string ContextId = "context-1";
    public static readonly DateTimeOffset StartTime = new(2026, 10, 9, 2, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;

    private ProviderSessionTestFixture(
        SqliteConnection connection,
        DbContextOptions<AgwDbContext> options,
        ServiceProvider services,
        TestTimeProvider time
    )
    {
        _connection = connection;
        Options = options;
        Services = services;
        Time = time;
        Context = new AgwDbContext(options);
        Service = CreateService(Context);
    }

    public DbContextOptions<AgwDbContext> Options { get; }

    public ServiceProvider Services { get; }

    public TestTimeProvider Time { get; }

    public AgwDbContext Context { get; }

    public TaskSessionBindingService Service { get; }

    public Guid ProjectId { get; } = Guid.CreateVersion7();

    public Guid ConversationId { get; } = Guid.CreateVersion7();

    public Guid AgentId { get; } = Guid.CreateVersion7();

    /// <summary>
    /// 创建测试环境；拦截器只加入协调器 scope 中的 DbContext，用来在协调器的保存过程中制造真实的取消或失败。调用方在同步代码中建立 Owner 用户上下文。
    /// Creates the environment; the interceptor is added only to the DbContext in coordinator scopes to cause real cancellations or failures during coordinator saves. Callers establish the Owner user context in synchronous code.
    /// </summary>
    public static async Task<ProviderSessionTestFixture> CreateAsync(IInterceptor? coordinatorInterceptor = null)
    {
        var token = TestContext.Current.CancellationToken;
        var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync(token);
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;
        var coordinatorOptions =
            coordinatorInterceptor == null
                ? options
                : new DbContextOptionsBuilder<AgwDbContext>(options).AddInterceptors(coordinatorInterceptor).Options;
        await using (var setup = new AgwDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync(token);
        }

        var time = new TestTimeProvider(StartTime);
        var fixture = new ProviderSessionTestFixture(
            connection,
            options,
            TestProjectPersistence.CreateProviderSessionServices(coordinatorOptions, time),
            time
        );
        await fixture.SeedProjectAsync(fixture.ProjectId, Owner);
        await fixture.SeedConversationAsync(fixture.ProjectId, fixture.ConversationId, ContextId, Owner);
        return fixture;
    }

    public TaskSessionBindingService CreateService(AgwDbContext context, IUserInfoService? userInfo = null) =>
        TestProjectPersistence.CreateBindingService(
            context,
            Services,
            new TestAgentCatalogFacade(new EfRepository<McpServer>(context)),
            userInfo
        );

    public Task<ProjectConversationBinding> SaveAsync(
        string providerSessionId,
        Guid? agentId = null,
        string externalAgentName = "codex",
        CancellationToken? cancellationToken = null
    ) =>
        Service.UpsertAsync(
            ProjectId,
            ContextId,
            agentId ?? AgentId,
            externalAgentName,
            providerSessionId,
            Owner,
            cancellationToken ?? TestContext.Current.CancellationToken
        );

    public async Task<List<ProjectConversationBinding>> ReadBindingsAsync(Guid? conversationId = null)
    {
        await using var context = new AgwDbContext(Options);
        using (UserInfoUtil.PushSystemScope())
        {
            var bindings = await context
                .ProjectConversationBindings.AsNoTracking()
                .Where(binding => binding.ProjectConversationId == (conversationId ?? ConversationId))
                .ToListAsync(TestContext.Current.CancellationToken);
            return bindings.OrderBy(binding => binding.CreateTime).ThenBy(binding => binding.Id).ToList();
        }
    }

    public async Task SeedProjectAsync(Guid projectId, string owner)
    {
        await using var context = new AgwDbContext(Options);
        context.Projects.Add(
            new Project
            {
                Id = projectId,
                Name = $"Project {projectId:N}",
                Type = ProjectType.UserDefined,
                CreateBy = owner,
                CreateTime = StartTime,
            }
        );
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async Task SeedConversationAsync(Guid projectId, Guid conversationId, string contextId, string owner)
    {
        await using var context = new AgwDbContext(Options);
        context.ProjectConversations.Add(
            new ProjectConversation
            {
                Id = conversationId,
                ProjectId = projectId,
                ContextId = contextId,
                Title = contextId,
                CreateBy = owner,
                CreateTime = StartTime,
            }
        );
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// 写入一条带有效清单的 durable 执行记录，状态由调用方指定。
    /// Writes a durable execution record with a valid manifest in the status chosen by the caller.
    /// </summary>
    public async Task SeedDurableExecutionAsync(DurableExecutionStatus status)
    {
        await using var context = new AgwDbContext(Options);
        var turnId = Guid.CreateVersion7();
        var now = Time.GetUtcNow();
        context.DurableExecutions.Add(
            new DurableExecutionRecord
            {
                Id = turnId,
                UserId = Owner,
                ProjectId = ProjectId,
                ProjectConversationId = ConversationId,
                ScopeBackfilled = true,
                ManifestJson = Agw.Shared.Utils.JsonUtil.Serialize(
                    new
                    {
                        schemaVersion = 1,
                        turnId,
                        userId = Owner,
                        workspaceSnapshot = Agw.Shared.Utils.ProjectWorkspacePaths.CreateSnapshot(ProjectId, null),
                        agentId = AgentId,
                        agentType = 0,
                        input = new { contents = Array.Empty<object>() },
                        settings = new { environmentVariables = new { }, resume = false },
                        task = new
                        {
                            taskId = Guid.CreateVersion7(),
                            projectId = ProjectId,
                            projectConversationId = ConversationId,
                            contextId = ContextId,
                        },
                    }
                ),
                Status = status,
                WorkerId = status == DurableExecutionStatus.Running ? "worker-1" : null,
                LeaseEpoch = status == DurableExecutionStatus.Running ? 1 : 0,
                LeaseExpiresAt = status == DurableExecutionStatus.Running ? now.AddMinutes(5) : null,
                StateChangedAt = now,
                StateVersion = Guid.CreateVersion7(),
                CreateBy = Owner,
                CreateTime = now,
                UpdateBy = Owner,
                UpdateTime = now,
            }
        );
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public static IDisposable PushUser(string userId) =>
        UserInfoUtil.Push(
            new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], authenticationType: "Test")
            )
        );

    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync();
        await Services.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
