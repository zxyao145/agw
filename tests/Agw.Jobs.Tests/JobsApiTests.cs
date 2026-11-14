using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Agw.Auth.Contracts;
using Agw.Events.Application;
using Agw.Events.Application.Delivery;
using Agw.Events.Contracts.Transport;
using Agw.Events.Infrastructure.Delivery;
using Agw.Events.Infrastructure.Dispatch;
using Agw.Infrastructure.Data;
using Agw.Jobs.Api;
using Agw.Jobs.Application.Services;
using Agw.Jobs.Contracts;
using Agw.Projects.Application.Persistence;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Shared.Results;
using Agw.Tests;
using Bens.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Agw.Jobs.Tests;

public class JobsApiTests
{
    [Theory]
    [Trait("Category", "Postgres")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_ProjectDeletedAfterJobRead_ReturnsConflict(bool moveJob)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PostgresJobTestDatabase.CreateAsync(
            PostgresJobTestDatabase.RequireAdminConnectionString(),
            cancellationToken
        );
        var jobRead = new JobsPostgresTests.JobReadGate();
        await using var fixture = await JobsApiFixture.CreateAsync(
            cancellationToken,
            JobsPostgresTests.PostgresSettings(database),
            services => services.ConfigureDbContext<AgwDbContext>(options => options.AddInterceptors(jobRead))
        );
        var current = await fixture.SeedTargetsAsync("tester", cancellationToken);
        var destination = moveJob ? await fixture.SeedTargetsAsync("tester", cancellationToken) : current;
        var job = await fixture.CreateViaApiAsync(CreateRequest(current, "Job before deletion"), cancellationToken);
        var request = new JobUpdateRequest
        {
            Id = job.Id,
            Name = "Updated job",
            ProjectId = destination.ProjectId,
            AgentId = current.AgentId,
            TargetKind = job.TargetKind,
            Instructions = job.Instructions,
            Schedule = new JobScheduleDto { Kind = "Interval", Interval = "PT5M" },
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var update = fixture.Client.PostAsJsonAsync("/api/jobs/update", request, timeout.Token);
        await DeleteProjectAfterJobReadAsync(fixture.Services, current.ProjectId, jobRead, timeout.Token);

        using var response = await update;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertErrorAsync(response, ErrorCodes.JobModificationConflict.Code, cancellationToken);
    }

    private static async Task DeleteProjectAfterJobReadAsync(
        IServiceProvider services,
        Guid projectId,
        JobsPostgresTests.JobReadGate jobRead,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await jobRead.WaitAsync(cancellationToken);
            using var user = UserInfoUtil.Push(CreatePrincipal("tester"));
            await using var deleteScope = services.CreateAsyncScope();
            var projects = deleteScope.ServiceProvider.GetRequiredService<IProjectDeletionCoordinator>();
            Assert.True(
                await projects.DeleteProjectAsync(new ProjectDeletionTarget(projectId, "tester"), cancellationToken)
            );
        }
        finally
        {
            jobRead.Release();
        }
    }

    [Fact]
    public async Task Create_CronJob_ReturnsEnvelopeWithDueAt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await JobsApiFixture.CreateAsync(cancellationToken);
        var targets = await fixture.SeedTargetsAsync("tester", cancellationToken);
        var request = new JobCreateRequest
        {
            Name = "Cron Job",
            ProjectId = targets.ProjectId,
            TargetKind = AgentRuntimeType.Agent,
            AgentId = targets.AgentId,
            Instructions = "Run the scheduled task.",
            Schedule = new JobScheduleDto { Kind = "Cron", Cron = "*/5 * * * *" },
        };

        using var response = await fixture.Client.PostAsJsonAsync("/api/jobs/create", request, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(0, body.RootElement.GetProperty("code").GetInt32());
        var data = body.RootElement.GetProperty("data");
        Assert.Equal("Cron", data.GetProperty("schedule").GetProperty("kind").GetString());
        Assert.Equal(0, data.GetProperty("targetKind").GetInt32());
        Assert.Equal(JsonValueKind.String, data.GetProperty("dueAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("lastDelivery").ValueKind);
        Assert.False(data.GetProperty("scheduleCompleted").GetBoolean());
        Assert.NotNull(data.Deserialize<JobResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Get_OnceWithoutDueAt_DistinguishesDisabledAndCompleted(bool completed)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await JobsApiFixture.CreateAsync(cancellationToken);
        var targets = await fixture.SeedTargetsAsync("tester", cancellationToken);
        var now = fixture.TimeProvider.GetUtcNow();
        var request = CreateRequest(
            targets,
            "Once Job",
            new JobScheduleDto { Kind = "Once", At = now.AddMinutes(completed ? -1 : 1) }
        );
        var job = await fixture.CreateViaApiAsync(request with { Enabled = completed }, cancellationToken);
        if (completed)
        {
            await fixture.RunScheduleAsync(job.Id, cancellationToken);
        }

        using var response = await fixture.Client.GetAsync($"/api/jobs/get?id={job.Id:D}", cancellationToken);
        using var body = await ReadBodyAsync(response, cancellationToken);
        var data = body.RootElement.GetProperty("data");
        Assert.Equal(JsonValueKind.Null, data.GetProperty("dueAt").ValueKind);
        Assert.Equal(completed, data.GetProperty("scheduleCompleted").GetBoolean());
    }

    [Theory]
    [InlineData("target-kind-out-of-range")]
    [InlineData("missing-schedule")]
    [InlineData("blank-instructions")]
    [InlineData("agent-of-other-user")]
    public async Task Create_InvalidRequest_ReturnsInvalidParamAndSavesNothing(string invalidCase)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await JobsApiFixture.CreateAsync(cancellationToken);
        var ownTargets = await fixture.SeedTargetsAsync("tester", cancellationToken);
        var otherTargets = await fixture.SeedTargetsAsync("other", cancellationToken);
        var request = CreateRequest(ownTargets, "Invalid Job");
        request = invalidCase switch
        {
            "target-kind-out-of-range" => request with { TargetKind = (AgentRuntimeType)2 },
            "missing-schedule" => request with { Schedule = null },
            "blank-instructions" => request with { Instructions = "  " },
            "agent-of-other-user" => request with { AgentId = otherTargets.AgentId },
            _ => throw new ArgumentOutOfRangeException(nameof(invalidCase)),
        };

        var response = await fixture.Client.PostAsJsonAsync("/api/jobs/create", request, cancellationToken);

        await AssertErrorAsync(response, ErrorCodes.InvalidParam.Code, cancellationToken);
        Assert.Empty(await fixture.ListEventSourcesAsync("tester", cancellationToken));
    }

    [Fact]
    public async Task Create_ProjectOfOtherUser_ReturnsResourceNotFound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await JobsApiFixture.CreateAsync(cancellationToken);
        var otherTargets = await fixture.SeedTargetsAsync("other", cancellationToken);

        var response = await fixture.Client.PostAsJsonAsync(
            "/api/jobs/create",
            CreateRequest(otherTargets, "Foreign Project Job"),
            cancellationToken
        );

        await AssertErrorAsync(response, ErrorCodes.ResourceNotFound.Code, cancellationToken);
    }

    [Theory]
    [InlineData("get")]
    [InlineData("update")]
    [InlineData("enable")]
    [InlineData("delete")]
    [InlineData("runs")]
    public async Task Request_OtherUsersJob_ReturnsJobNotFound(string operation)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await JobsApiFixture.CreateAsync(cancellationToken);
        var otherTargets = await fixture.SeedTargetsAsync("other", cancellationToken);
        var job = await fixture.CreateJobAsUserAsync(
            "other",
            CreateRequest(otherTargets, "Other User Job"),
            cancellationToken
        );

        var response = operation switch
        {
            "get" => await fixture.Client.GetAsync($"/api/jobs/get?id={job.Id:D}", cancellationToken),
            "update" => await fixture.Client.PostAsJsonAsync(
                "/api/jobs/update",
                new JobUpdateRequest
                {
                    Id = job.Id,
                    Name = "Changed",
                    ProjectId = otherTargets.ProjectId,
                    TargetKind = AgentRuntimeType.Agent,
                    AgentId = otherTargets.AgentId,
                    Instructions = "Changed instructions",
                    Schedule = new JobScheduleDto { Kind = "Interval", Interval = "PT5M" },
                },
                cancellationToken
            ),
            "enable" => await fixture.Client.PostAsJsonAsync(
                "/api/jobs/enable",
                new JobEnableRequest { Id = job.Id, Enabled = false },
                cancellationToken
            ),
            "delete" => await fixture.Client.PostAsJsonAsync(
                "/api/jobs/delete",
                new JobIdRequest { Id = job.Id },
                cancellationToken
            ),
            "runs" => await fixture.Client.GetAsync(
                $"/api/jobs/runs/list?jobId={job.Id:D}&offset=0&limit=100",
                cancellationToken
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

        await AssertErrorAsync(response, ErrorCodes.JobNotFound.Code, cancellationToken);
    }

    [Fact]
    public async Task List_ProjectFilter_ReturnsOnlyThatProject()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await JobsApiFixture.CreateAsync(cancellationToken);
        var firstTargets = await fixture.SeedTargetsAsync("tester", cancellationToken);
        var secondTargets = await fixture.SeedTargetsAsync("tester", cancellationToken);
        var firstJob = await fixture.CreateViaApiAsync(
            CreateRequest(firstTargets, "First Project Job"),
            cancellationToken
        );
        await fixture.CreateViaApiAsync(CreateRequest(secondTargets, "Second Project Job"), cancellationToken);

        using var response = await fixture.Client.GetAsync(
            $"/api/jobs/list?projectId={firstTargets.ProjectId:D}",
            cancellationToken
        );

        using var body = await ReadBodyAsync(response, cancellationToken);
        Assert.Equal(0, body.RootElement.GetProperty("code").GetInt32());
        var job = Assert.Single(body.RootElement.GetProperty("data").EnumerateArray());
        Assert.Equal(firstJob.Id, job.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Enable_DisableThenEnable_ReturnsDueAtNullThenDueAt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await JobsApiFixture.CreateAsync(cancellationToken);
        var targets = await fixture.SeedTargetsAsync("tester", cancellationToken);
        var job = await fixture.CreateViaApiAsync(CreateRequest(targets, "Toggle Job"), cancellationToken);

        using var disabled = await fixture.Client.PostAsJsonAsync(
            "/api/jobs/enable",
            new JobEnableRequest { Id = job.Id, Enabled = false },
            cancellationToken
        );
        using (var body = await ReadBodyAsync(disabled, cancellationToken))
        {
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("data").GetProperty("dueAt").ValueKind);
        }

        using var enabled = await fixture.Client.PostAsJsonAsync(
            "/api/jobs/enable",
            new JobEnableRequest { Id = job.Id, Enabled = true },
            cancellationToken
        );
        using var enabledBody = await ReadBodyAsync(enabled, cancellationToken);
        Assert.Equal(JsonValueKind.String, enabledBody.RootElement.GetProperty("data").GetProperty("dueAt").ValueKind);
    }

    [Fact]
    public async Task Delete_ExistingJob_ReturnsJobAndGetReturnsJobNotFound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await JobsApiFixture.CreateAsync(cancellationToken);
        var targets = await fixture.SeedTargetsAsync("tester", cancellationToken);
        var job = await fixture.CreateViaApiAsync(CreateRequest(targets, "Deleted Job"), cancellationToken);

        using var response = await fixture.Client.PostAsJsonAsync(
            "/api/jobs/delete",
            new JobIdRequest { Id = job.Id },
            cancellationToken
        );

        using (var body = await ReadBodyAsync(response, cancellationToken))
        {
            Assert.Equal(0, body.RootElement.GetProperty("code").GetInt32());
            Assert.Equal(job.Id, body.RootElement.GetProperty("data").GetProperty("id").GetGuid());
        }
        var get = await fixture.Client.GetAsync($"/api/jobs/get?id={job.Id:D}", cancellationToken);
        await AssertErrorAsync(get, ErrorCodes.JobNotFound.Code, cancellationToken);
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, 0)]
    [InlineData(0, 501)]
    public async Task RunsList_OffsetOrLimitOutOfRange_ReturnsInvalidParam(int offset, int limit)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await JobsApiFixture.CreateAsync(cancellationToken);
        var targets = await fixture.SeedTargetsAsync("tester", cancellationToken);
        var job = await fixture.CreateViaApiAsync(CreateRequest(targets, "Runs Job"), cancellationToken);

        var response = await fixture.Client.GetAsync(
            $"/api/jobs/runs/list?jobId={job.Id:D}&offset={offset}&limit={limit}",
            cancellationToken
        );

        await AssertErrorAsync(response, ErrorCodes.InvalidParam.Code, cancellationToken);
    }

    [Fact]
    public async Task RunsList_CancelledDelivery_ReturnsFailedRunWithConversationId()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await JobsApiFixture.CreateAsync(cancellationToken);
        var targets = await fixture.SeedTargetsAsync("tester", cancellationToken);
        var request = CreateRequest(
            targets,
            "Cancelled Run Job",
            new JobScheduleDto { Kind = "Once", At = fixture.TimeProvider.GetUtcNow().AddMinutes(-1) }
        );
        var job = await fixture.CreateViaApiAsync(request, cancellationToken);
        var delivery = await fixture.RunJobToWaitingExecutionAsync(job.Id, cancellationToken);
        await fixture.CancelDeliveryAsync(delivery.Id, cancellationToken);
        var conversationId = await fixture.ResolveConversationIdAsync(delivery.TurnId, cancellationToken);

        using var response = await fixture.Client.GetAsync(
            $"/api/jobs/runs/list?jobId={job.Id:D}&offset=0&limit=100",
            cancellationToken
        );

        using var body = await ReadBodyAsync(response, cancellationToken);
        Assert.Equal(0, body.RootElement.GetProperty("code").GetInt32());
        var run = Assert.Single(body.RootElement.GetProperty("data").EnumerateArray());
        Assert.Equal(2, run.GetProperty("result").GetInt32());
        Assert.Equal(conversationId.ToString("D"), run.GetProperty("conversationId").GetString());
        Assert.Equal("Cancelled Run Job", await fixture.GetConversationTitleAsync(conversationId, cancellationToken));
    }

    private static JobCreateRequest CreateRequest(
        SeededTargets targets,
        string? name,
        JobScheduleDto? schedule = null
    ) =>
        new()
        {
            Name = name,
            ProjectId = targets.ProjectId,
            TargetKind = AgentRuntimeType.Agent,
            AgentId = targets.AgentId,
            Instructions = "Run the scheduled task.",
            Schedule = schedule ?? new JobScheduleDto { Kind = "Interval", Interval = "PT5M" },
        };

    private static async Task<JsonDocument> ReadBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken
    ) => JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

    private static async Task AssertErrorAsync(
        HttpResponseMessage response,
        int expectedCode,
        CancellationToken cancellationToken
    )
    {
        using (response)
        using (var body = await ReadBodyAsync(response, cancellationToken))
        {
            Assert.Equal(expectedCode, body.RootElement.GetProperty("code").GetInt32());
        }
    }

    private sealed record SeededTargets(Guid ProjectId, Guid AgentId);

    private sealed record WaitingDelivery(Guid Id, Guid TurnId);

    private sealed class JobsApiFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly WebApplication _app;

        private JobsApiFixture(string root, WebApplication app)
        {
            _root = root;
            _app = app;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }
        public IServiceProvider Services => _app.Services;
        public TimeProvider TimeProvider => _app.Services.GetRequiredService<TimeProvider>();

        public async Task<SeededTargets> SeedTargetsAsync(string ownerUserId, CancellationToken cancellationToken)
        {
            var projectId = Guid.CreateVersion7();
            var agentId = Guid.CreateVersion7();
            var workspace = Path.Combine(_root, $"workspace-{projectId:N}");
            Directory.CreateDirectory(workspace);
            using var userScope = UserInfoUtil.Push(CreatePrincipal(ownerUserId));
            await using var scope = _app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
            db.Projects.Add(
                new Project
                {
                    Id = projectId,
                    Name = $"Project {projectId:N}",
                    Workspace = workspace,
                }
            );
            db.Agents.Add(
                new Agent
                {
                    Id = agentId,
                    Name = $"agent-{agentId:N}",
                    DisplayName = "Test Agent",
                }
            );
            await db.SaveChangesAsync(cancellationToken);
            return new SeededTargets(projectId, agentId);
        }

        public async Task<IReadOnlyList<EventSource>> ListEventSourcesAsync(
            string ownerUserId,
            CancellationToken cancellationToken
        )
        {
            using var userScope = UserInfoUtil.Push(CreatePrincipal(ownerUserId));
            await using var scope = _app.Services.CreateAsyncScope();
            return await scope
                .ServiceProvider.GetRequiredService<AgwDbContext>()
                .EventSources.AsNoTracking()
                .ToArrayAsync(cancellationToken);
        }

        public async Task<JobResponse> CreateViaApiAsync(JobCreateRequest request, CancellationToken cancellationToken)
        {
            using var response = await Client.PostAsJsonAsync("/api/jobs/create", request, cancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = await ReadBodyAsync(response, cancellationToken);
            Assert.Equal(0, body.RootElement.GetProperty("code").GetInt32());
            return body.RootElement.GetProperty("data")
                    .Deserialize<JobResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new Xunit.Sdk.XunitException("The create endpoint returned no JobResponse.");
        }

        public async Task<JobResponse> CreateJobAsUserAsync(
            string ownerUserId,
            JobCreateRequest request,
            CancellationToken cancellationToken
        )
        {
            using var userScope = UserInfoUtil.Push(CreatePrincipal(ownerUserId));
            await using var scope = _app.Services.CreateAsyncScope();
            return await scope
                .ServiceProvider.GetRequiredService<JobAppService>()
                .CreateAsync(request, cancellationToken);
        }

        public async Task<WaitingDelivery> RunJobToWaitingExecutionAsync(
            Guid jobId,
            CancellationToken cancellationToken
        )
        {
            await RunScheduleAsync(jobId, cancellationToken);
            using var userScope = UserInfoUtil.Push(CreatePrincipal("tester"));
            await using var scope = _app.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var db = services.GetRequiredService<AgwDbContext>();
            var transport = services.GetRequiredService<IEventTransport>();
            var accepted = await ModuleCompositionTestHost.ReadTransportDeliveryAsync(transport, EventTopics.Accepted);
            await services.GetRequiredService<EventDispatcher>().DispatchAsync(accepted, cancellationToken);
            var deliveryMessage = await ModuleCompositionTestHost.ReadTransportDeliveryAsync(
                transport,
                EventTopics.Delivery
            );
            await services
                .GetRequiredService<EventDeliveryProcessor>()
                .ProcessAsync(deliveryMessage, cancellationToken);

            var jobKey = jobId.ToString("D");
            var subscription = await db
                .EventSubscriptions.AsNoTracking()
                .SingleAsync(row => row.ManagedKey == jobKey, cancellationToken);
            var delivery = await db
                .EventDeliveries.AsNoTracking()
                .SingleAsync(row => row.SubscriptionId == subscription.Id, cancellationToken);
            Assert.Equal(EventDeliveryState.WaitingExecution, delivery.State);
            Assert.True(delivery.TurnId.HasValue);
            return new WaitingDelivery(delivery.Id, delivery.TurnId.Value);
        }

        public async Task RunScheduleAsync(Guid jobId, CancellationToken cancellationToken)
        {
            using var userScope = UserInfoUtil.Push(CreatePrincipal("tester"));
            await using var scope = _app.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var db = services.GetRequiredService<AgwDbContext>();
            var jobKey = jobId.ToString("D");
            var source = await db
                .EventSources.AsNoTracking()
                .SingleAsync(row => row.ManagedKey == jobKey, cancellationToken);
            var schedule = await db
                .EventSchedules.AsNoTracking()
                .SingleAsync(row => row.CallerKey == $"source:{source.Id}", cancellationToken);
            Assert.True(
                await services.GetRequiredService<EventScheduleRunner>().RunAsync(schedule.Id, cancellationToken)
            );
        }

        public async Task CancelDeliveryAsync(Guid deliveryId, CancellationToken cancellationToken)
        {
            using var userScope = UserInfoUtil.Push(CreatePrincipal("tester"));
            await using var scope = _app.Services.CreateAsyncScope();
            Assert.True(
                await scope
                    .ServiceProvider.GetRequiredService<EventDeliveryManagementAppService>()
                    .CancelAsync(deliveryId, cancellationToken)
            );
        }

        public async Task<Guid> ResolveConversationIdAsync(Guid turnId, CancellationToken cancellationToken)
        {
            using var userScope = UserInfoUtil.Push(CreatePrincipal("tester"));
            await using var scope = _app.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var existing = await services
                .GetRequiredService<Agw.Projects.Contracts.Execution.IProjectTaskFacade>()
                .ResolveConversationIdsAsync([turnId], cancellationToken);
            return existing.TryGetValue(turnId, out var conversationId)
                ? conversationId
                : throw new Xunit.Sdk.XunitException($"Turn {turnId} has no project conversation.");
        }

        public async Task<string> GetConversationTitleAsync(Guid conversationId, CancellationToken cancellationToken)
        {
            using var userScope = UserInfoUtil.Push(CreatePrincipal("tester"));
            await using var scope = _app.Services.CreateAsyncScope();
            return await scope
                .ServiceProvider.GetRequiredService<AgwDbContext>()
                .ProjectConversations.AsNoTracking()
                .Where(row => row.Id == conversationId)
                .Select(row => row.Title)
                .SingleAsync(cancellationToken);
        }

        public static async Task<JobsApiFixture> CreateAsync(
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string?>? settings = null,
            Action<IServiceCollection>? configureServices = null
        )
        {
            var root = Path.Combine(
                AppContext.BaseDirectory,
                ".scratch",
                "jobs-api",
                Guid.CreateVersion7().ToString("N")
            );
            var builder = WebApplication.CreateBuilder(
                new WebApplicationOptions { EnvironmentName = Environments.Production }
            );
            builder.WebHost.UseTestServer();
            builder.Services.AddApiResult();
            ModuleCompositionTestHost.ConfigureServices(builder.Services, root, settings);
            builder.Services.RemoveAll<IHostedService>();
            configureServices?.Invoke(builder.Services);

            var app = builder.Build();
            app.UseMiddleware<AgwApiExceptionMiddleware>();
            app.Use(
                async (context, next) =>
                {
                    context.User = CreatePrincipal("tester");
                    using var userScope = UserInfoUtil.Push(context.User);
                    await next(context);
                }
            );
            app.MapJobsApi();
            await ModuleCompositionTestHost.InitializeAsync(app.Services);
            await app.StartAsync(cancellationToken);
            return new JobsApiFixture(root, app);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private static ClaimsPrincipal CreatePrincipal(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], authenticationType: "Test"));
}
