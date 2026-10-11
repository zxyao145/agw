using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Contracts.Catalog;
using Agw.Auth.Application;
using Agw.Infrastructure.Agents;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Projects;
using Agw.Infrastructure.Repositories;
using Agw.Projects.Application.Facades;
using Agw.Projects.Application.Persistence;
using Agw.Projects.Contracts.Execution;
using Agw.Projects.Controllers;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Shared.Results;
using Agw.Testing;
using Bens.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Projects.Tests;

public sealed class ProjectProviderSessionsControllerTests : IDisposable
{
    private const string Owner = ProviderSessionTestFixture.Owner;
    private const string Route = "/api/projects/conversations/provider-sessions";
    private readonly IDisposable _user = ProviderSessionTestFixture.PushUser(Owner);

    public void Dispose() => _user.Dispose();

    [Fact]
    public async Task List_ConversationWithHistory_ReturnsEveryRecordInBensResultsEnvelope()
    {
        await using var api = await ProviderSessionsApi.CreateAsync();
        var first = await api.SaveAsync("session-a");
        api.Time.SetUtcNow(ProviderSessionTestFixture.StartTime.AddHours(1));
        var second = await api.SaveAsync("session-b");

        using var response = await api.Client.GetAsync(
            $"{Route}?projectId={api.ProjectId}&conversationId={api.ConversationId}",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Equal(0, body.RootElement.GetProperty("code").GetInt32());
        var items = body.RootElement.GetProperty("data").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal(second.Id, items[0].GetProperty("id").GetGuid());
        Assert.Equal(api.AgentId, items[0].GetProperty("agentId").GetGuid());
        Assert.Equal("codex", items[0].GetProperty("externalAgentName").GetString());
        Assert.Equal("session-b", items[0].GetProperty("providerSessionId").GetString());
        Assert.True(items[0].GetProperty("isActive").GetBoolean());
        Assert.Equal(
            ProviderSessionTestFixture.StartTime.AddHours(1),
            items[0].GetProperty("createTime").GetDateTimeOffset()
        );
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("updateTime").ValueKind);
        Assert.Equal(first.Id, items[1].GetProperty("id").GetGuid());
        Assert.False(items[1].GetProperty("isActive").GetBoolean());
        Assert.Equal(
            ProviderSessionTestFixture.StartTime.AddHours(1),
            items[1].GetProperty("updateTime").GetDateTimeOffset()
        );
    }

    [Fact]
    public async Task List_ConversationWithoutBindings_ReturnsEmptyArray()
    {
        await using var api = await ProviderSessionsApi.CreateAsync();

        using var response = await api.Client.GetAsync(
            $"{Route}?projectId={api.ProjectId}&conversationId={api.ConversationId}",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Empty(body.RootElement.GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task List_MissingIdentifiers_ReturnsInvalidParam()
    {
        await using var api = await ProviderSessionsApi.CreateAsync();

        using var response = await api.Client.GetAsync(
            $"{Route}?projectId={api.ProjectId}",
            TestContext.Current.CancellationToken
        );

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, ErrorCodes.InvalidParam);
    }

    [Fact]
    public async Task List_OtherUser_ReturnsNotFound()
    {
        await using var api = await ProviderSessionsApi.CreateAsync();
        await api.SaveAsync("session-a");

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{Route}?projectId={api.ProjectId}&conversationId={api.ConversationId}"
        );
        request.Headers.Add(ProviderSessionsApi.UserHeader, "other-user");
        using var response = await api.Client.SendAsync(request, TestContext.Current.CancellationToken);

        await AssertErrorAsync(response, HttpStatusCode.NotFound, ErrorCodes.ResourceNotFound);
    }

    [Fact]
    public async Task List_Unauthenticated_ReturnsAuthenticationRequired()
    {
        await using var api = await ProviderSessionsApi.CreateAsync();

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{Route}?projectId={api.ProjectId}&conversationId={api.ConversationId}"
        );
        request.Headers.Add(ProviderSessionsApi.UserHeader, ProviderSessionsApi.AnonymousUser);
        using var response = await api.Client.SendAsync(request, TestContext.Current.CancellationToken);

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, ErrorCodes.AuthenticationRequired);
    }

    [Fact]
    public async Task Archive_ActiveBinding_ArchivesAndRepeatsIdempotently()
    {
        await using var api = await ProviderSessionsApi.CreateAsync();
        var binding = await api.SaveAsync("session-a");
        var archiveTime = ProviderSessionTestFixture.StartTime.AddHours(1);
        api.Time.SetUtcNow(archiveTime);

        using var first = await api.ArchiveAsync(binding.Id);
        api.Time.SetUtcNow(archiveTime.AddHours(1));
        using var second = await api.ArchiveAsync(binding.Id);

        foreach (var response in new[] { first, second })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = await ReadJsonAsync(response);
            Assert.Equal(0, body.RootElement.GetProperty("code").GetInt32());
            Assert.Equal("OK", body.RootElement.GetProperty("title").GetString());
        }

        var stored = Assert.Single(await api.ReadBindingsAsync());
        Assert.False(stored.IsActive);
        Assert.Equal(Owner, stored.UpdateBy);
        Assert.Equal(archiveTime, stored.UpdateTime);
    }

    [Fact]
    public async Task Archive_InvalidBody_ReturnsInvalidParam()
    {
        await using var api = await ProviderSessionsApi.CreateAsync();

        using var response = await api.Client.PostAsJsonAsync(
            $"{Route}/archive",
            new { projectId = api.ProjectId, conversationId = api.ConversationId },
            TestContext.Current.CancellationToken
        );

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, ErrorCodes.InvalidParam);
    }

    [Fact]
    public async Task Archive_OtherUsersBinding_ReturnsNotFound()
    {
        await using var api = await ProviderSessionsApi.CreateAsync();
        var binding = await api.SaveAsync("session-a");

        using var response = await api.ArchiveAsync(binding.Id, user: "other-user");

        await AssertErrorAsync(response, HttpStatusCode.NotFound, ErrorCodes.ResourceNotFound);
        Assert.True(Assert.Single(await api.ReadBindingsAsync()).IsActive);
    }

    [Fact]
    public async Task Archive_ExecutingConversation_ReturnsConflict()
    {
        await using var api = await ProviderSessionsApi.CreateAsync();
        var binding = await api.SaveAsync("session-a");
        await using var scope = api.Services.CreateAsyncScope();
        var gate = scope.ServiceProvider.GetRequiredService<IConversationExecutionGate>();

        await using (await gate.AcquireAsync(api.ConversationId, 0, TestContext.Current.CancellationToken))
        {
            using var response = await api.ArchiveAsync(binding.Id);
            await AssertErrorAsync(response, HttpStatusCode.Conflict, ErrorCodes.ConversationSessionConflict);
        }

        Assert.True(Assert.Single(await api.ReadBindingsAsync()).IsActive);
    }

    [Fact]
    public async Task ArchiveThenSave_SameExecutionConnection_StartsNewSessionAndRejectsArchivedSession()
    {
        await using var api = await ProviderSessionsApi.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        await using var connection = api.Services.CreateAsyncScope();
        var providerSessions = connection.ServiceProvider.GetRequiredService<IProjectProviderSessionFacade>();
        var reference = new ProjectProviderSessionReference(
            api.ProjectId,
            ProviderSessionTestFixture.ContextId,
            api.AgentId,
            "codex"
        );
        Assert.Equal(
            "session-a",
            await providerSessions.SaveProviderSessionIdAsync(reference, "session-a", Owner, token)
        );
        var archived = Assert.Single(await api.ReadBindingsAsync());
        var archiveTime = ProviderSessionTestFixture.StartTime.AddHours(1);
        api.Time.SetUtcNow(archiveTime);

        using (var response = await api.ArchiveAsync(archived.Id))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Null(await providerSessions.GetProviderSessionIdAsync(reference, token));
        api.Time.SetUtcNow(archiveTime.AddHours(1));
        Assert.Equal(
            "session-b",
            await providerSessions.SaveProviderSessionIdAsync(reference, "session-b", Owner, token)
        );

        var bindings = await api.ReadBindingsAsync();
        var first = Assert.Single(bindings, binding => binding.Id == archived.Id);
        Assert.False(first.IsActive);
        Assert.Equal(Owner, first.UpdateBy);
        Assert.Equal(archiveTime, first.UpdateTime);
        var active = Assert.Single(bindings, binding => binding.IsActive);
        Assert.Equal("session-b", active.ProviderSessionId);
        Assert.Equal(archiveTime.AddHours(1), active.CreateTime);
        Assert.Equal("session-b", await providerSessions.GetProviderSessionIdAsync(reference, token));

        var error = await Assert.ThrowsAsync<AgwException>(() =>
            providerSessions.SaveProviderSessionIdAsync(reference, "session-a", Owner, token)
        );
        Assert.Equal(ErrorCodes.ConversationSessionConflict.Code, error.Code);
        Assert.Equal(2, (await api.ReadBindingsAsync()).Count);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, ErrorCode error)
    {
        Assert.Equal(status, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Equal(error.Code, body.RootElement.GetProperty("code").GetInt32());
    }

    /// <summary>
    /// 只组装本功能所需服务的真实 ASP.NET Core 管线：路由、模型绑定、AgwApiExceptionMiddleware 与 Bens.Results 均为生产实现。
    /// A real ASP.NET Core pipeline assembling only the services this feature needs: routing, model binding, AgwApiExceptionMiddleware and Bens.Results are the production implementations.
    /// </summary>
    private sealed class ProviderSessionsApi : IAsyncDisposable
    {
        public const string UserHeader = "X-Test-User";
        public const string AnonymousUser = "anonymous";

        private readonly WebApplication _app;
        private readonly SqliteConnection _connection;
        private HttpClient? _client;

        private ProviderSessionsApi(WebApplication app, SqliteConnection connection, TestTimeProvider time)
        {
            _app = app;
            _connection = connection;
            Time = time;
        }

        public HttpClient Client => _client ??= _app.GetTestClient();

        public TestTimeProvider Time { get; }

        public IServiceProvider Services => _app.Services;

        public Guid ProjectId { get; } = Guid.CreateVersion7();

        public Guid ConversationId { get; } = Guid.CreateVersion7();

        public Guid AgentId { get; } = Guid.CreateVersion7();

        public static async Task<ProviderSessionsApi> CreateAsync()
        {
            var token = TestContext.Current.CancellationToken;
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
            await connection.OpenAsync(token);
            var time = new TestTimeProvider(ProviderSessionTestFixture.StartTime);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            var services = builder.Services;
            services.AddApiResult();
            services.AddControllers().AddApplicationPart(typeof(ProjectProviderSessionsController).Assembly);
            services.Configure<ApiBehaviorOptions>(options =>
                options.InvalidModelStateResponseFactory = context =>
                    ApiResult.BadRequest(context.ModelState, code: ErrorCodes.InvalidParam.Code)
            );
            services.AddSingleton<TimeProvider>(time);
            services.AddSingleton<IApplicationLock>(InMemoryApplicationLock.Shared);
            services.AddDbContext<AgwDbContext>(options =>
                options.UseSqlite(connection).UseSnakeCaseNamingConvention()
            );
            services.AddScoped<IProjectsDbContext>(provider => provider.GetRequiredService<AgwDbContext>());
            services.AddScoped<IUserInfoService, UserInfoService>();
            services.AddScoped<IAgentCatalogFacade>(provider => new TestAgentCatalogFacade(
                new EfRepository<McpServer>(provider.GetRequiredService<AgwDbContext>())
            ));
            services.AddScoped<IConversationExecutionGate, ConversationExecutionGate>();
            services.AddScoped<IDurableExecutionScopeMaintenance, DurableExecutionScopeMaintenance>();
            services.AddScoped<IProjectProviderSessionCoordinator, ProjectProviderSessionCoordinator>();
            services.AddScoped<ITaskSessionBindingService, TaskSessionBindingService>();
            services.AddScoped<IProjectProviderSessionFacade, ProjectProviderSessionFacade>();

            var app = builder.Build();
            app.Use(
                async (context, next) =>
                {
                    var userId = context.Request.Headers.TryGetValue(UserHeader, out var header)
                        ? header.ToString()
                        : Owner;
                    var principal =
                        userId == AnonymousUser
                            ? new ClaimsPrincipal(new ClaimsIdentity())
                            : new ClaimsPrincipal(
                                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "Test")
                            );
                    context.User = principal;
                    using var userScope = UserInfoUtil.Push(principal);
                    await next();
                }
            );
            app.UseMiddleware<AgwApiExceptionMiddleware>();
            app.MapControllers();

            var api = new ProviderSessionsApi(app, connection, time);
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
                await context.Database.EnsureCreatedAsync(token);
                context.Projects.Add(
                    new Project
                    {
                        Id = api.ProjectId,
                        Name = "Provider Sessions",
                        Type = ProjectType.UserDefined,
                        CreateBy = Owner,
                        CreateTime = ProviderSessionTestFixture.StartTime,
                    }
                );
                context.ProjectConversations.Add(
                    new ProjectConversation
                    {
                        Id = api.ConversationId,
                        ProjectId = api.ProjectId,
                        ContextId = ProviderSessionTestFixture.ContextId,
                        Title = "Provider Sessions",
                        CreateBy = Owner,
                        CreateTime = ProviderSessionTestFixture.StartTime,
                    }
                );
                await context.SaveChangesAsync(token);
            }

            await app.StartAsync(token);
            return api;
        }

        public async Task<ProjectConversationBinding> SaveAsync(string providerSessionId)
        {
            await using var scope = _app.Services.CreateAsyncScope();
            return await scope
                .ServiceProvider.GetRequiredService<ITaskSessionBindingService>()
                .UpsertAsync(
                    ProjectId,
                    ProviderSessionTestFixture.ContextId,
                    AgentId,
                    "codex",
                    providerSessionId,
                    Owner,
                    TestContext.Current.CancellationToken
                );
        }

        public Task<HttpResponseMessage> ArchiveAsync(Guid bindingId, string? user = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{Route}/archive")
            {
                Content = JsonContent.Create(
                    new
                    {
                        projectId = ProjectId,
                        conversationId = ConversationId,
                        bindingId,
                    }
                ),
            };
            if (user != null)
            {
                request.Headers.Add(UserHeader, user);
            }

            return Client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        public async Task<List<ProjectConversationBinding>> ReadBindingsAsync()
        {
            await using var scope = _app.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
            var bindings = await context
                .ProjectConversationBindings.AsNoTracking()
                .Where(binding => binding.ProjectConversationId == ConversationId)
                .ToListAsync(TestContext.Current.CancellationToken);
            return bindings.OrderBy(binding => binding.CreateTime).ToList();
        }

        public async ValueTask DisposeAsync()
        {
            _client?.Dispose();
            await _app.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
