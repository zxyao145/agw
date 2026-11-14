using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agw.Agents.Execution.Context;
using Agw.Agents.Execution.HumanInteraction.InProcess;
using Agw.Agents.Execution.Turns;
using Agw.Auth.Contracts;
using Agw.Infrastructure.Data;
using Agw.Jobs.Application.Skills;
using Agw.Jobs.Contracts.Tools;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Agw.Skills.Contracts.Registration;
using Agw.Tests;
using Agw.Tools.Abstractions.Generated;
using Agw.Tools.Generated;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Jobs.Tests;

public class JobManagementSkillTests
{
    [Fact]
    public async Task SkillMetadata_ProvidesInstructionsAndScheduleReference()
    {
        await using var fixture = await JobManagementSkillFixture.CreateAsync();
        var skill = fixture.Registration.Create(Guid.CreateVersion7());

        Assert.Equal("agw-job", skill.Frontmatter.Name);
        var resource = await skill.GetResourceAsync("job-trigger-reference", TestContext.Current.CancellationToken);
        Assert.NotNull(resource);
        var resourceResult = await resource.ReadAsync(serviceProvider: null, TestContext.Current.CancellationToken);
        var content = resourceResult is JsonElement resourceElement
            ? resourceElement.GetString()
            : Assert.IsType<string>(resourceResult);
        Assert.NotNull(content);
        Assert.Contains("ISO 8601", content, StringComparison.Ordinal);
        Assert.Contains("targetKind", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateJobAsync_IntervalJob_ReturnsScheduleAndDueAt()
    {
        await using var fixture = await JobManagementSkillFixture.CreateAsync();
        var targets = await fixture.SeedTargetsAsync("tester");
        var tools = fixture.CreateTools(targets.ProjectId);
        using var context = fixture.PushInteractiveContext(targets.ProjectId, "tester");

        var created = await RunToolAsync<JobToolResponse>(
            tools,
            "agw_job_create",
            new
            {
                instructions = "Run the scheduled task.",
                targetKind = "Agent",
                agentId = targets.AgentId,
                kind = "Interval",
                interval = "PT15M",
                name = "Interval Job",
            }
        );

        Assert.Equal("Interval", created.Schedule.Kind);
        Assert.Equal("PT15M", created.Schedule.Interval);
        Assert.NotNull(created.DueAt);
    }

    [Fact]
    public async Task UpdateJobAsync_NoFields_ThrowsNoChangesToMake()
    {
        await using var fixture = await JobManagementSkillFixture.CreateAsync();
        var targets = await fixture.SeedTargetsAsync("tester");
        var tools = fixture.CreateTools(targets.ProjectId);
        using var context = fixture.PushInteractiveContext(targets.ProjectId, "tester");
        var created = await CreateJobAsync(tools, targets.AgentId);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            RunToolAsync<JobToolResponse>(tools, "agw_job_update", new { jobId = created.Id })
        );

        Assert.Equal(ErrorCodes.NoChangesToMake.Code, exception.Code);
    }

    [Fact]
    public async Task UpdateJobAsync_OnlyCron_ReplacesCronAndKeepsOtherFields()
    {
        await using var fixture = await JobManagementSkillFixture.CreateAsync();
        var targets = await fixture.SeedTargetsAsync("tester");
        var tools = fixture.CreateTools(targets.ProjectId);
        using var context = fixture.PushInteractiveContext(targets.ProjectId, "tester");
        var created = await RunToolAsync<JobToolResponse>(
            tools,
            "agw_job_create",
            new
            {
                instructions = "Run the scheduled task.",
                targetKind = "Agent",
                agentId = targets.AgentId,
                kind = "Cron",
                cron = "0 9 * * 1-5",
                name = "Weekday Job",
                enabled = false,
            }
        );

        var updated = await RunToolAsync<JobToolResponse>(
            tools,
            "agw_job_update",
            new { jobId = created.Id, cron = "*/10 * * * *" }
        );

        Assert.Equal(created.Id, updated.Id);
        Assert.Equal(created.Name, updated.Name);
        Assert.Equal(created.Instructions, updated.Instructions);
        Assert.False(updated.Enabled);
        Assert.Equal("Cron", updated.Schedule.Kind);
        Assert.Equal("*/10 * * * *", updated.Schedule.Cron);
    }

    [Fact]
    public async Task GetJobAsync_JobInOtherProject_ThrowsJobNotFound()
    {
        await using var fixture = await JobManagementSkillFixture.CreateAsync();
        var firstTargets = await fixture.SeedTargetsAsync("tester");
        var secondTargets = await fixture.SeedTargetsAsync("tester");
        var firstTools = fixture.CreateTools(firstTargets.ProjectId);
        using var context = fixture.PushInteractiveContext(firstTargets.ProjectId, "tester");
        var created = await CreateJobAsync(firstTools, firstTargets.AgentId);
        var secondTools = fixture.CreateTools(secondTargets.ProjectId);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            RunToolAsync<JobToolResponse>(secondTools, "agw_job_get", new { jobId = created.Id })
        );

        Assert.Equal(ErrorCodes.JobNotFound.Code, exception.Code);
    }

    [Fact]
    public async Task DeleteJobAsync_ConfirmationMismatch_ThrowsInvalidParam()
    {
        await using var fixture = await JobManagementSkillFixture.CreateAsync();
        var targets = await fixture.SeedTargetsAsync("tester");
        var tools = fixture.CreateTools(targets.ProjectId);
        using var context = fixture.PushInteractiveContext(targets.ProjectId, "tester");
        var created = await CreateJobAsync(tools, targets.AgentId);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            RunToolAsync<JobToolResponse>(
                tools,
                "agw_job_delete",
                new { jobId = created.Id, confirmation = Guid.CreateVersion7().ToString("D") }
            )
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    private static Task<JobToolResponse> CreateJobAsync(IReadOnlyDictionary<string, AIFunction> tools, Guid agentId) =>
        RunToolAsync<JobToolResponse>(
            tools,
            "agw_job_create",
            new
            {
                instructions = "Run the scheduled task.",
                targetKind = "Agent",
                agentId,
                kind = "Interval",
                interval = "PT15M",
                name = "Interval Job",
            }
        );

    private static async Task<T> RunToolAsync<T>(
        IReadOnlyDictionary<string, AIFunction> tools,
        string toolName,
        object arguments
    )
    {
        var input = JsonSerializer
            .SerializeToElement(arguments)
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value);
        var result = await tools[toolName]
            .InvokeAsync(new AIFunctionArguments(input), TestContext.Current.CancellationToken);
        if (result is T typed)
        {
            return typed;
        }

        var json = result is JsonElement element ? element.GetRawText() : JsonSerializer.Serialize(result);
        var serializerOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        serializerOptions.Converters.Add(new JsonStringEnumConverter());
        return JsonSerializer.Deserialize<T>(json, serializerOptions)
            ?? throw new Xunit.Sdk.XunitException($"Tool '{toolName}' returned no {typeof(T).Name} result.");
    }

    private sealed record SeededTargets(Guid ProjectId, Guid AgentId);

    private sealed class JobManagementSkillFixture : IAsyncDisposable
    {
        private readonly ModuleCompositionTestHost _host;
        private readonly string _workspaceRoot;

        private JobManagementSkillFixture(ModuleCompositionTestHost host)
        {
            _host = host;
            _workspaceRoot = Path.Combine(
                AppContext.BaseDirectory,
                ".scratch",
                "jobs-skill-tests",
                Guid.CreateVersion7().ToString("N")
            );
            Registration = host
                .Services.GetServices<IAgentSkillRegistration>()
                .OfType<JobManagementSkillRegistration>()
                .Single();
            GeneratedCatalog = host.Services.GetRequiredService<AgwGeneratedToolCatalog>();
        }

        public JobManagementSkillRegistration Registration { get; }
        private AgwGeneratedToolCatalog GeneratedCatalog { get; }

        public IReadOnlyList<AgwGeneratedToolDescriptor> ToolDeclarations =>
            Registration.ToolTypes.SelectMany(type => GeneratedCatalog.GetTools(type)).ToArray();

        public IReadOnlyDictionary<string, AIFunction> CreateTools(Guid projectId) =>
            ToolDeclarations.ToDictionary(
                tool => tool.Name,
                tool => Assert.IsAssignableFrom<AIFunction>(GeneratedCatalog.Create(tool, projectId))
            );

        public IDisposable PushInteractiveContext(Guid projectId, string userId)
        {
            var agentId = Guid.CreateVersion7();
            var turnId = Guid.CreateVersion7();
            var context = new AgentExecutionContext
            {
                UserId = userId,
                ProjectId = projectId,
                ProjectConversationId = Guid.CreateVersion7(),
                ContextId = Guid.CreateVersion7().ToString("D"),
                Generation = 0,
                WorkspaceSnapshot = new ProjectWorkspaceSnapshot("/workspace", [], "fingerprint"),
                TurnId = turnId,
                TurnTargetId = agentId,
                RuntimeType = AgentRuntimeType.Agent,
                AgentId = agentId,
                EngineKind = EngineKind.Maf,
                Provider = ExecutionProvider.InProcess,
            };
            var userScope = UserInfoUtil.Push(CreatePrincipal(userId));
            var output = new TurnBroadcast(turnId, userId, _host.Services.GetRequiredService<TimeProvider>());
            var executionScope = ExecutionScope
                .Create(context, turnId, output, new InMemoryPendingInteractionSet(null))
                .Push();
            return new CompositeScope(executionScope, userScope);
        }

        public async Task<SeededTargets> SeedTargetsAsync(string ownerUserId)
        {
            var projectId = Guid.CreateVersion7();
            var agentId = Guid.CreateVersion7();
            var workspace = Path.Combine(_workspaceRoot, $"workspace-{projectId:N}");
            Directory.CreateDirectory(workspace);
            using var userScope = UserInfoUtil.Push(CreatePrincipal(ownerUserId));
            await using var scope = _host.Services.CreateAsyncScope();
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
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return new SeededTargets(projectId, agentId);
        }

        public static async Task<JobManagementSkillFixture> CreateAsync()
        {
            var host = await ModuleCompositionTestHost.CreateAsync();
            return new JobManagementSkillFixture(host);
        }

        public async ValueTask DisposeAsync()
        {
            await _host.DisposeAsync();
            if (Directory.Exists(_workspaceRoot))
            {
                Directory.Delete(_workspaceRoot, recursive: true);
            }
        }

        private sealed class CompositeScope : IDisposable
        {
            private readonly IDisposable _executionScope;
            private readonly IDisposable _userScope;

            public CompositeScope(IDisposable executionScope, IDisposable userScope)
            {
                _executionScope = executionScope;
                _userScope = userScope;
            }

            public void Dispose()
            {
                _executionScope.Dispose();
                _userScope.Dispose();
            }
        }
    }

    private static ClaimsPrincipal CreatePrincipal(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], authenticationType: "Test"));
}
