using Agw.Jobs.Execution;
using Agw.Projects.Contracts.Execution;
using Agw.Shared.Data.Entities.Jobs;

namespace Agw.Jobs.Tests;

public sealed class JobAgentExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_JobOwnerAndTarget_ArePassedThroughFacade()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var executionId = Guid.CreateVersion7();
        var agentId = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();
        var agentExecutions = new RecordingAgentExecutionFacade();
        var projectTasks = new RecordingProjectTaskFacade();
        var executor = new JobAgentExecutor(agentExecutions, projectTasks);
        var job = new Job
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            AgentType = AgentRuntimeType.Agent,
            AgentId = agentId,
            Name = "Scheduled agent",
            Prompt = "run",
            CreateBy = "job-owner",
        };

        await executor.ExecuteAsync(job, executionId, cancellationToken);

        Assert.NotNull(agentExecutions.Request);
        Assert.Equal("job-owner", agentExecutions.Request.OwnerUserId);
        Assert.Equal(executionId, agentExecutions.Request.ExecutionId);
        Assert.Equal(agentId, agentExecutions.Request.Target.Id);
        Assert.Equal(AgentTargetKind.Agent, agentExecutions.Request.Target.Kind);
        Assert.Equal(projectId, projectTasks.Request?.ProjectId);
    }

    private sealed class RecordingAgentExecutionFacade : IAgentExecutionFacade
    {
        public AgentExecutionRequest? Request { get; private set; }

        public Task<AgentExecutionResult> ExecuteAsync(
            AgentExecutionRequest request,
            CancellationToken cancellationToken = default
        )
        {
            Request = request;
            return Task.FromResult(new AgentExecutionResult(request.ExecutionId, AgentExecutionState.Completed, []));
        }

        public async IAsyncEnumerable<AgentExecutionEvent> ExecuteStreamingAsync(
            AgentExecutionRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class RecordingProjectTaskFacade : IProjectTaskFacade
    {
        public StartProjectTaskRequest? Request { get; private set; }

        public Task<ProjectTaskSnapshot> ResolveAsync(
            ResolveProjectTaskRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<ProjectTaskSnapshot?> GetAsync(Guid taskId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ProjectTaskSnapshot?>(null);

        public Task<ProjectTaskSnapshot> GetOrCreateAsync(
            StartProjectTaskRequest request,
            CancellationToken cancellationToken = default
        )
        {
            Request = request;
            return Task.FromResult(
                new ProjectTaskSnapshot(
                    request.TaskId,
                    Guid.CreateVersion7(),
                    request.ProjectId,
                    request.ContextId ?? request.TaskId.ToString("D"),
                    request.JobId,
                    request.Title ?? "Scheduled Job",
                    request.InitialStatus,
                    null,
                    DateTimeOffset.UtcNow,
                    null,
                    null
                )
            );
        }

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
