using System.Text.Json;
using System.Text.Json.Serialization;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Connections;
using Agw.Agents.Execution.Turns;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Jobs.Application.Services;
using Agw.Jobs.Application.Skills;
using Agw.Jobs.Scheduling;
using Agw.Jobs.Scheduling.Coordination;
using Agw.Projects.Contracts.Execution;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
using Agw.Testing;
using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Jobs.Tests;

public class JobManagementSkillTests
{
    private static readonly DateTimeOffset UtcNow = new(2026, 7, 26, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SkillMetadata_MafDiscoversFrontmatterResourceAndFiveScripts()
    {
        await using var fixture = await JobManagementSkillFixture.CreateAsync();
        var skill = fixture.CreateSkill(Guid.CreateVersion7());

        Assert.Equal("agw-job", skill.Frontmatter.Name);
        Assert.Contains("scheduled jobs", skill.Frontmatter.Description, StringComparison.OrdinalIgnoreCase);

        var resource = await skill.GetResourceAsync("job-trigger-reference", TestContext.Current.CancellationToken);
        Assert.NotNull(resource);
        var resourceResult = await resource.ReadAsync(serviceProvider: null, TestContext.Current.CancellationToken);
        var content = resourceResult is JsonElement resourceElement
            ? resourceElement.GetString()
            : Assert.IsType<string>(resourceResult);
        Assert.NotNull(content);
        Assert.Contains("RFC 3339", content, StringComparison.Ordinal);
        Assert.Contains("five-field cron", content, StringComparison.Ordinal);

        foreach (var scriptName in new[] { "list-jobs", "get-job", "create-job", "update-job", "delete-job" })
        {
            Assert.NotNull(await skill.GetScriptAsync(scriptName, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task ListAndGetJobs_WithoutInteractiveContext_StayWithinCapturedProject()
    {
        await using var fixture = await JobManagementSkillFixture.CreateAsync();
        var projectId = Guid.CreateVersion7();
        var otherProjectId = Guid.CreateVersion7();
        var projectJob = await fixture.SeedJobAsync(projectId, "Project job");
        var otherJob = await fixture.SeedJobAsync(otherProjectId, "Other job");
        var skill = fixture.CreateSkill(projectId);

        var jobs = await RunScriptAsync<JobSkillResponse[]>(skill, "list-jobs", new { });

        var listed = Assert.Single(jobs);
        Assert.Equal(projectJob.Id, listed.Id);
        Assert.Equal(projectId, listed.ProjectId);

        var found = await RunScriptAsync<JobSkillResponse>(skill, "get-job", new { jobId = projectJob.Id });
        Assert.Equal(projectJob.Id, found.Id);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            RunScriptAsync<JobSkillResponse>(skill, "get-job", new { jobId = otherJob.Id })
        );
        Assert.Equal(ErrorCodes.JobNotFound.Code, exception.Code);
    }

    [Fact]
    public async Task CreateJob_UsesInteractiveProjectAndUserAndCalculatesNextRun()
    {
        await using var fixture = await JobManagementSkillFixture.CreateAsync();
        var projectId = Guid.CreateVersion7();
        var agentId = Guid.CreateVersion7();
        var skill = fixture.CreateSkill(projectId);
        using var context = fixture.PushInteractiveContext(projectId, "skill-user");

        var created = await RunScriptAsync<JobSkillResponse>(
            skill,
            "create-job",
            new
            {
                prompt = "Run a status check",
                agentType = AgentRuntimeType.Agent,
                agentId,
                triggerType = TriggerType.Interval,
                triggerValue = "00:15:00",
                name = "Status check",
                maxRetryCount = 5,
                isEnabled = true,
            }
        );

        Assert.Equal(projectId, created.ProjectId);
        Assert.Equal(agentId, created.AgentId);
        Assert.Equal(UtcNow.AddMinutes(15), created.NextRunTime);
        Assert.DoesNotContain(
            created.GetType().GetProperties(),
            property => string.Equals(property.Name, "RowVersion", StringComparison.Ordinal)
        );

        var persisted = await fixture.GetJobAsync(created.Id);
        Assert.Equal("skill-user", persisted.CreateBy);
        Assert.Equal("skill-user", persisted.UpdateBy);
        Assert.Equal(projectId, persisted.ProjectId);
    }

    [Fact]
    public async Task UpdateJob_PatchPreservesOmittedFieldsAndSupportsClearPrompt()
    {
        await using var fixture = await JobManagementSkillFixture.CreateAsync();
        var projectId = Guid.CreateVersion7();
        var existing = await fixture.SeedJobAsync(projectId, "Existing job", "Keep or clear");
        var skill = fixture.CreateSkill(projectId);
        using var context = fixture.PushInteractiveContext(projectId, "patch-user");

        var updated = await RunScriptAsync<JobSkillResponse>(
            skill,
            "update-job",
            new
            {
                jobId = existing.Id,
                clearPrompt = true,
                isEnabled = false,
            }
        );

        Assert.Equal(existing.Name, updated.Name);
        Assert.Equal(existing.AgentType, updated.AgentType);
        Assert.Equal(existing.AgentId, updated.AgentId);
        Assert.Equal(existing.TriggerType, updated.TriggerType);
        Assert.Equal(existing.TriggerValue, updated.TriggerValue);
        Assert.Equal(existing.NextRunTime, updated.NextRunTime);
        Assert.Null(updated.Prompt);
        Assert.False(updated.IsEnabled);

        var persisted = await fixture.GetJobAsync(existing.Id);
        Assert.Equal("patch-user", persisted.UpdateBy);
    }

    [Fact]
    public async Task UpdateJob_TriggerPatchRecalculatesNextRunTime()
    {
        await using var fixture = await JobManagementSkillFixture.CreateAsync();
        var projectId = Guid.CreateVersion7();
        var existing = await fixture.SeedJobAsync(projectId, "Existing job");
        var skill = fixture.CreateSkill(projectId);
        using var context = fixture.PushInteractiveContext(projectId, "schedule-user");

        var updated = await RunScriptAsync<JobSkillResponse>(
            skill,
            "update-job",
            new { jobId = existing.Id, triggerValue = "01:00:00" }
        );

        Assert.Equal(TriggerType.Interval, updated.TriggerType);
        Assert.Equal("01:00:00", updated.TriggerValue);
        Assert.Equal(UtcNow.AddHours(1), updated.NextRunTime);
        Assert.Equal(existing.Prompt, updated.Prompt);
        Assert.Equal(existing.AgentId, updated.AgentId);
    }

    [Fact]
    public async Task UpdateJob_RejectsEmptyPatchAndIncompleteAgentTarget()
    {
        await using var fixture = await JobManagementSkillFixture.CreateAsync();
        var projectId = Guid.CreateVersion7();
        var existing = await fixture.SeedJobAsync(projectId, "Existing job");
        var skill = fixture.CreateSkill(projectId);
        using var context = fixture.PushInteractiveContext(projectId, "patch-user");

        var emptyPatch = await Assert.ThrowsAsync<AgwException>(() =>
            RunScriptAsync<JobSkillResponse>(skill, "update-job", new { jobId = existing.Id })
        );
        Assert.Equal(ErrorCodes.NoChangesToMake.Code, emptyPatch.Code);

        var incompleteAgentTarget = await Assert.ThrowsAsync<AgwException>(() =>
            RunScriptAsync<JobSkillResponse>(
                skill,
                "update-job",
                new { jobId = existing.Id, agentType = AgentRuntimeType.Agentflow }
            )
        );
        Assert.Equal(ErrorCodes.InvalidParam.Code, incompleteAgentTarget.Code);

        var blankPrompt = await Assert.ThrowsAsync<AgwException>(() =>
            RunScriptAsync<JobSkillResponse>(skill, "update-job", new { jobId = existing.Id, prompt = " " })
        );
        Assert.Equal(ErrorCodes.InvalidParam.Code, blankPrompt.Code);
    }

    [Fact]
    public async Task WriteScripts_WithoutMatchingInteractiveContext_AreRejected()
    {
        await using var fixture = await JobManagementSkillFixture.CreateAsync();
        var projectId = Guid.CreateVersion7();
        var existing = await fixture.SeedJobAsync(projectId, "Existing job");
        var skill = fixture.CreateSkill(projectId);

        var createException = await Assert.ThrowsAsync<AgwException>(() =>
            RunScriptAsync<JobSkillResponse>(
                skill,
                "create-job",
                new
                {
                    prompt = "Run",
                    agentType = AgentRuntimeType.Agent,
                    agentId = Guid.CreateVersion7(),
                    triggerType = TriggerType.Interval,
                    triggerValue = "00:05:00",
                }
            )
        );
        Assert.Equal(ErrorCodes.InteractiveAdminRequired.Code, createException.Code);

        using var wrongProjectConversation = fixture.PushInteractiveContext(
            Guid.CreateVersion7(),
            "wrong-project-user"
        );
        var updateException = await Assert.ThrowsAsync<AgwException>(() =>
            RunScriptAsync<JobSkillResponse>(skill, "update-job", new { jobId = existing.Id, isEnabled = false })
        );
        Assert.Equal(ErrorCodes.InteractiveAdminRequired.Code, updateException.Code);

        var deleteException = await Assert.ThrowsAsync<AgwException>(() =>
            RunScriptAsync<JobSkillResponse>(
                skill,
                "delete-job",
                new { jobId = existing.Id, confirmation = existing.Id.ToString() }
            )
        );
        Assert.Equal(ErrorCodes.InteractiveAdminRequired.Code, deleteException.Code);
    }

    [Fact]
    public async Task DeleteJob_RequiresExactConfirmationAndProjectScope()
    {
        await using var fixture = await JobManagementSkillFixture.CreateAsync();
        var projectId = Guid.CreateVersion7();
        var otherProjectId = Guid.CreateVersion7();
        var projectJob = await fixture.SeedJobAsync(projectId, "Project job");
        var otherJob = await fixture.SeedJobAsync(otherProjectId, "Other job");
        var skill = fixture.CreateSkill(projectId);
        using var context = fixture.PushInteractiveContext(projectId, "delete-user");

        var confirmationException = await Assert.ThrowsAsync<AgwException>(() =>
            RunScriptAsync<JobSkillResponse>(
                skill,
                "delete-job",
                new { jobId = projectJob.Id, confirmation = Guid.CreateVersion7().ToString() }
            )
        );
        Assert.Equal(ErrorCodes.InvalidParam.Code, confirmationException.Code);

        var scopedException = await Assert.ThrowsAsync<AgwException>(() =>
            RunScriptAsync<JobSkillResponse>(
                skill,
                "delete-job",
                new { jobId = otherJob.Id, confirmation = otherJob.Id.ToString() }
            )
        );
        Assert.Equal(ErrorCodes.JobNotFound.Code, scopedException.Code);

        var deleted = await RunScriptAsync<JobSkillResponse>(
            skill,
            "delete-job",
            new { jobId = projectJob.Id, confirmation = projectJob.Id.ToString() }
        );
        Assert.Equal(projectJob.Id, deleted.Id);
        Assert.Equal(projectId, deleted.ProjectId);
        Assert.Equal(projectJob.TriggerValue, deleted.TriggerValue);
        Assert.Null(await fixture.GetJobOrDefaultAsync(projectJob.Id));
        Assert.NotNull(await fixture.GetJobOrDefaultAsync(otherJob.Id));
    }

    private static async Task<T> RunScriptAsync<T>(AgentSkill skill, string scriptName, object arguments)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var script = await skill.GetScriptAsync(scriptName, cancellationToken);
        Assert.NotNull(script);
        var result = await script.RunAsync(
            skill,
            JsonSerializer.SerializeToElement(arguments),
            serviceProvider: null,
            cancellationToken
        );

        if (result is T typed)
        {
            return typed;
        }

        var json = result is JsonElement element ? element.GetRawText() : JsonSerializer.Serialize(result);
        var serializerOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        serializerOptions.Converters.Add(new JsonStringEnumConverter());
        return JsonSerializer.Deserialize<T>(json, serializerOptions)
            ?? throw new Xunit.Sdk.XunitException($"Script '{scriptName}' returned no {typeof(T).Name} result.");
    }

    private sealed class JobManagementSkillFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _serviceProvider;

        private JobManagementSkillFixture(
            SqliteConnection connection,
            ServiceProvider serviceProvider,
            TestRuntimeTurnContextAccessor turnContextAccessor,
            JobManagementSkillRegistration registration
        )
        {
            _connection = connection;
            _serviceProvider = serviceProvider;
            TurnContextAccessor = turnContextAccessor;
            Registration = registration;
        }

        private TestRuntimeTurnContextAccessor TurnContextAccessor { get; }

        private JobManagementSkillRegistration Registration { get; }

        public AgentSkill CreateSkill(Guid projectId) => Registration.Create(projectId);

        public IDisposable PushInteractiveContext(Guid projectId, string userId)
        {
            var contextId = Guid.CreateVersion7().ToString("D");
            return TurnContextAccessor.Push(
                new RuntimeTurnContext(
                    ExecutionSettings.FromCommand(new SettingCommand(projectId)),
                    new AgentExecutionTask
                    {
                        TaskId = Guid.CreateVersion7(),
                        ProjectConversationId = Guid.CreateVersion7(),
                        ProjectId = projectId,
                        ContextId = contextId,
                        CreateTime = TimeProvider.System.GetUtcNow(),
                    },
                    new ExecutionTarget(Guid.CreateVersion7(), AgentRuntimeType.Agent),
                    workspace: string.Empty,
                    messageSink: null!
                )
                {
                    UserId = userId,
                }
            );
        }

        public static async Task<JobManagementSkillFixture> CreateAsync()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(cancellationToken);
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .UseSnakeCaseNamingConvention()
                .Options;
            var timeProvider = new TestTimeProvider(UtcNow);
            var turnContextAccessor = new TestRuntimeTurnContextAccessor();
            var services = new ServiceCollection();
            services.AddSingleton<TimeProvider>(timeProvider);
            services.AddSingleton<IRuntimeTurnContextAccessor>(turnContextAccessor);
            services.AddSingleton<ICurrentAgentTurn>(turnContextAccessor);
            services.AddSingleton<IProjectTaskFacade, NoopProjectTaskFacade>();
            services.AddSingleton<JobScheduleCalculator>();
            services.AddSingleton<JobSchedulerWakeSignal>();
            services.AddScoped(_ => new AgwDbContext(options));
            services.AddScoped<DbContext>(serviceProvider => serviceProvider.GetRequiredService<AgwDbContext>());
            services.AddScoped<IUnitOfWork>(serviceProvider => serviceProvider.GetRequiredService<AgwDbContext>());
            services.AddScoped<IRepository<Job>>(serviceProvider => new JobRepo(
                serviceProvider.GetRequiredService<DbContext>(),
                serviceProvider.GetRequiredService<TimeProvider>()
            ));
            services.AddScoped<IRepository<JobLog>, EfRepository<JobLog>>();
            services.AddScoped<
                IRepository<ProjectConversationChatHistory>,
                EfRepository<ProjectConversationChatHistory>
            >();
            services.AddScoped<IRepository<ProjectConversation>, EfRepository<ProjectConversation>>();
            services.AddScoped<JobAppService>();
            var serviceProvider = services.BuildServiceProvider();

            await using (var scope = serviceProvider.CreateAsyncScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
                await dbContext.Database.EnsureCreatedAsync(cancellationToken);
            }

            var registration = new JobManagementSkillRegistration(
                serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                turnContextAccessor
            );
            return new JobManagementSkillFixture(connection, serviceProvider, turnContextAccessor, registration);
        }

        public async Task<Job> SeedJobAsync(Guid projectId, string name, string? prompt = "Prompt")
        {
            await using var scope = _serviceProvider.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
            var job = new Job
            {
                Id = Guid.CreateVersion7(),
                ProjectId = projectId,
                AgentType = AgentRuntimeType.Agent,
                AgentId = Guid.CreateVersion7(),
                Name = name,
                Prompt = prompt,
                TriggerType = TriggerType.Interval,
                TriggerValue = "00:30:00",
                NextRunTime = UtcNow.AddMinutes(10),
                Status = JobStatus.Pending,
                IsEnabled = true,
                MaxRetryCount = 3,
                CreateBy = "seed",
                CreateTime = UtcNow,
                UpdateBy = "seed",
                UpdateTime = UtcNow,
            };
            dbContext.Jobs.Add(job);
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            return job;
        }

        public async Task<Job> GetJobAsync(Guid id) =>
            await GetJobOrDefaultAsync(id) ?? throw new Xunit.Sdk.XunitException($"Job {id} was not found.");

        public async Task<Job?> GetJobOrDefaultAsync(Guid id)
        {
            await using var scope = _serviceProvider.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
            return await dbContext
                .Jobs.AsNoTracking()
                .SingleOrDefaultAsync(job => job.Id == id, TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await _serviceProvider.DisposeAsync();
            await _connection.DisposeAsync();
        }

        private sealed class TestRuntimeTurnContextAccessor : IRuntimeTurnContextAccessor, ICurrentAgentTurn
        {
            public RuntimeTurnContext? Current { get; private set; }

            AgentTurnSnapshot? ICurrentAgentTurn.Current =>
                Current == null ? null : new AgentTurnSnapshot(Current.ProjectId, Current.UserId);

            public IDisposable Push(RuntimeTurnContext context)
            {
                var previous = Current;
                Current = context;
                return new PopScope(this, previous);
            }

            private sealed class PopScope : IDisposable
            {
                private readonly TestRuntimeTurnContextAccessor _accessor;
                private readonly RuntimeTurnContext? _previous;

                public PopScope(TestRuntimeTurnContextAccessor accessor, RuntimeTurnContext? previous)
                {
                    _accessor = accessor;
                    _previous = previous;
                }

                public void Dispose() => _accessor.Current = _previous;
            }
        }

        private sealed class NoopProjectTaskFacade : IProjectTaskFacade
        {
            public Task<ProjectTaskSnapshot> ResolveAsync(
                ResolveProjectTaskRequest request,
                CancellationToken cancellationToken = default
            ) => throw new NotSupportedException();

            public Task<ProjectTaskSnapshot?> GetAsync(Guid taskId, CancellationToken cancellationToken = default) =>
                Task.FromResult<ProjectTaskSnapshot?>(null);

            public Task<ProjectTaskSnapshot> GetOrCreateAsync(
                StartProjectTaskRequest request,
                CancellationToken cancellationToken = default
            ) => throw new NotSupportedException();

            public Task<ProjectTaskSnapshot?> FinishAsync(
                FinishProjectTaskRequest request,
                CancellationToken cancellationToken = default
            ) => Task.FromResult<ProjectTaskSnapshot?>(null);

            public Task<IReadOnlyDictionary<Guid, string?>> ResolveContextIdsAsync(
                IReadOnlyCollection<Guid> taskIds,
                CancellationToken cancellationToken = default
            ) => Task.FromResult<IReadOnlyDictionary<Guid, string?>>(new Dictionary<Guid, string?>());
        }
    }
}
