using System.Security.Claims;
using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Agents;
using Agw.Agents.Execution.Agents.Dtos;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Runtimes;
using Agw.Auth.Application;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Jobs.Execution;
using Agw.Projects.Application;
using Agw.Projects.Domain.Services;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Agw.Jobs.Tests;

public sealed class JobAgentExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_UsesJobOwnerForRuntimeAndRestoresPreviousUser()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var originalUser = UserInfoUtil.Current;
        var previousUser = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "caller")], "Test")
        );
        UserInfoUtil.Current = previousUser;

        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(cancellationToken);
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .UseSnakeCaseNamingConvention()
                .Options;
            await using var dbContext = new AgwDbContext(options);
            await dbContext.Database.EnsureCreatedAsync(cancellationToken);
            var project = new Project
            {
                Id = Guid.CreateVersion7(),
                Name = "Job project",
                Type = ProjectType.UserDefined,
                CreateBy = "job-owner",
                CreateTime = TimeProvider.System.GetUtcNow(),
            };
            dbContext.Projects.Add(project);
            await dbContext.SaveChangesAsync(cancellationToken);

            var taskExecution = new TaskExecutionAppService(
                new EfRepository<ProjectConversation>(dbContext),
                new EfRepository<ProjectConversationChatHistory>(dbContext),
                dbContext,
                new ProjectConversationChatHistoryDomainService(),
                new ProjectResolver(new EfRepository<Project>(dbContext)),
                TimeProvider.System
            );
            var runtime = new RecordingAgentRuntimeService();
            var executor = new JobAgentExecutor(runtime, new ThrowingAgentflowRuntimeService(), taskExecution);
            var job = new Job
            {
                Id = Guid.CreateVersion7(),
                ProjectId = project.Id,
                AgentType = AgentRuntimeType.Agent,
                AgentId = Guid.CreateVersion7(),
                Name = "Scheduled agent",
                Prompt = "run",
                CreateBy = "job-owner",
            };

            await executor.ExecuteAsync(job, Guid.CreateVersion7(), cancellationToken);

            Assert.Equal("job-owner", runtime.ObservedUserId);
            Assert.Same(previousUser, UserInfoUtil.Current);
        }
        finally
        {
            UserInfoUtil.Current = originalUser;
        }
    }

    private sealed class RecordingAgentRuntimeService : IAgentRuntimeService
    {
        public string? ObservedUserId { get; private set; }

        public Task<AIAgent?> CreateAiAgentAsync(Guid agentId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AIAgent?> CreateAiAgentAsync(
            Guid agentId,
            Guid? projectId,
            bool resume,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<AIAgent?> CreateAiAgentAsync(
            Guid agentId,
            Guid? projectId,
            bool resume,
            IReadOnlyDictionary<string, string>? environmentVariables,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<AgentRuntime?> CreateRuntimeAsync(
            Guid agentId,
            TaskProjection task,
            SettingCommand settings,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
            AgentRuntime session,
            AgwUserInput input,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<AgwMessage>> ExecuteAsync(
            AgentRuntime session,
            AgwUserInput input,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<AgentExecutionResult?> ExecuteByIdAsync(
            AgentExecuteByIdRequest request,
            CancellationToken cancellationToken = default
        )
        {
            ObservedUserId = UserInfoUtil.UserId;
            return Task.FromResult<AgentExecutionResult?>(
                new AgentExecutionResult(request.TaskId?.ToString("D") ?? "task", request.ContextId ?? "context", [])
            );
        }
    }

    private sealed class ThrowingAgentflowRuntimeService : IAgentflowRuntimeService
    {
        public IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
            Guid agentflowId,
            string input,
            CancellationToken cancellationToken = default,
            Guid? projectId = null,
            string? contextId = null,
            Guid? taskId = null,
            IHumanGateApprovalHandler? humanGateApprovalHandler = null,
            IReadOnlyDictionary<string, string>? environmentVariables = null,
            Guid? conversationId = null,
            PermissionMode? permissionMode = null
        ) => throw new NotSupportedException();

        public Task<AgentflowExecutionResult?> ExecuteAsync(
            Guid agentflowId,
            Guid taskId,
            string input,
            CancellationToken cancellationToken = default,
            Guid? projectId = null,
            string? contextId = null
        ) => throw new NotSupportedException();

        public Task<AgentflowExecutionResult?> ExecuteAsync(
            Guid agentflowId,
            Guid taskId,
            List<ChatMessage> messages,
            CancellationToken cancellationToken = default,
            Guid? projectId = null,
            string? contextId = null
        ) => throw new NotSupportedException();

        public Task<AgentflowWorkflowLease?> CreateAiWorkflow(
            Guid agentflowId,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<string?> GetMermaidAsync(Guid agentflowId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
