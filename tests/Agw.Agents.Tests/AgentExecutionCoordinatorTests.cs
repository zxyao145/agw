using Agw.Api.Contracts;
using Agw.Api.Execution;
using Agw.Shared.Enums;
using Agw.Shared.Tasks;
using Agw.Shared.Tasks.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public class AgentExecutionCoordinatorTests
{
    [Fact]
    public async Task NormalizeSettingsAsync_WhenTaskExists_MarksResumeTrue()
    {
        var taskAppService = new FakeTaskAppService
        {
            HasTaskResult = true
        };
        var coordinator = CreateCoordinator(taskAppService: taskAppService);
        var settings = new SettingCommand(Guid.NewGuid(), Guid.NewGuid(), "{}");

        var normalized = await coordinator.NormalizeSettingsAsync(settings, CancellationToken.None);

        Assert.True(normalized.Resume);
    }

    [Fact]
    public async Task ResolveTaskAsync_WhenResumeTaskMissing_ReturnsBadRequest()
    {
        var coordinator = CreateCoordinator(
            taskAppService: new FakeTaskAppService { GetTaskResult = null },
            projectAppService: new FakeProjectAppService { ResolvedProjectId = Guid.NewGuid() });

        var result = await coordinator.ResolveTaskAsync(
            new ExecutionTaskRequest(
                ExecutionId: Guid.NewGuid(),
                AgentType: AgentRuntimeType.Agent,
                TaskId: Guid.NewGuid(),
                ProjectId: Guid.NewGuid(),
                Input: "hello",
                Resume: true,
                User: "tester"),
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Error);
        Assert.Equal("Task not found.", badRequest.Value);
    }

    [Fact]
    public async Task ResolveTaskAsync_WhenTaskMissing_CreatesTask()
    {
        var projectId = Guid.NewGuid();
        var createdTask = new ProjectTask
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            ContextId = "ctx-1",
            AgentType = AgentRuntimeType.Agent
        };
        var taskAppService = new FakeTaskAppService
        {
            GetTaskResult = null,
            CreateTaskResult = createdTask
        };
        var coordinator = CreateCoordinator(
            taskAppService: taskAppService,
            projectAppService: new FakeProjectAppService { ResolvedProjectId = projectId });

        var result = await coordinator.ResolveTaskAsync(
            new ExecutionTaskRequest(
                ExecutionId: Guid.NewGuid(),
                AgentType: AgentRuntimeType.Agent,
                TaskId: Guid.NewGuid(),
                ProjectId: projectId,
                Input: "hello",
                Resume: false,
                User: "tester"),
            CancellationToken.None);

        Assert.Same(createdTask, result.Task);
        Assert.Null(result.Error);
        Assert.Equal("tester", taskAppService.LastCreateUser);
    }

    private static IAgentExecutionCoordinator CreateCoordinator(
        ITaskAppService? taskAppService = null,
        IProjectAppService? projectAppService = null)
    {
        return new AgentExecutionCoordinator(
            agentRuntimeService: null!,
            agentflowRuntimeService: null!,
            taskAppService: taskAppService ?? new FakeTaskAppService(),
            projectAppService: projectAppService ?? new FakeProjectAppService(),
            logger: NullLogger<AgentExecutionCoordinator>.Instance);
    }

    private sealed class FakeTaskAppService : ITaskAppService
    {
        public ProjectTask? GetTaskResult { get; set; }

        public ProjectTask? CreateTaskResult { get; set; }

        public bool HasTaskResult { get; set; }

        public string? LastCreateUser { get; private set; }

        public Task<ProjectTask?> GetTaskAsync(Guid value) => Task.FromResult(GetTaskResult);

        public Task<ProjectTask?> CreateTaskForExecutionAsync(
            Guid projectId,
            Guid? taskId,
            AgentRuntimeType agentType,
            Guid executionId,
            string input,
            string user,
            CancellationToken cancellationToken = default)
        {
            LastCreateUser = user;
            return Task.FromResult(CreateTaskResult);
        }

        public Task<bool> HasTaskAsync(Guid taskId, Guid? projectId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(HasTaskResult);
    }

    private sealed class FakeProjectAppService : IProjectAppService
    {
        public Guid? ResolvedProjectId { get; set; }

        public Task<IReadOnlyList<Project>> ListAsync(System.Linq.Expressions.Expression<Func<Project, bool>>? predicate = null) =>
            Task.FromResult<IReadOnlyList<Project>>([]);

        public Task<string?> GetProjectExtraSettingAsync(Guid? projectId) => Task.FromResult<string?>(null);

        public Task<Guid?> ResolveProjectIdAsync(Guid? projectId) => Task.FromResult(ResolvedProjectId);

        public Task<Project?> CreateAsync(Project project, string user) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id) => throw new NotSupportedException();

        public Task<Project?> GetAsync(Guid id) => throw new NotSupportedException();

        public Task<Project?> UpdateAsync(Guid id, Action<Project> updateAction, string user) => throw new NotSupportedException();
    }
}
