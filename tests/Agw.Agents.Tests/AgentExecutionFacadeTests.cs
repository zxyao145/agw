using System.Security.Claims;
using Agw.Agents.Contracts.Catalog;
using Agw.Agents.Execution.Inbound.Facades;
using Agw.Agents.Execution.Turns;
using Agw.Projects.Contracts.Execution;
using Agw.Projects.Contracts.Runtime;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Tests;

public sealed class AgentExecutionFacadeTests : IAsyncLifetime
{
    private readonly TestAgentDatabase _database = new();
    private readonly InProcessCoordinatorTestKit _kit = new();
    private TurnPersistenceTestKit _persistence = null!;

    public async ValueTask InitializeAsync() => _persistence = await TurnPersistenceTestKit.CreateAsync();

    public async ValueTask DisposeAsync()
    {
        _database.Dispose();
        await _persistence.DisposeAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(AgentExecutionPermissionMode.FullAccess)]
    public async Task ExecuteAsync_InProcess_RunsTurnAndRestoresUserContext(
        AgentExecutionPermissionMode? permissionMode
    )
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var agentId = await AddAgentAsync("owner", cancellationToken);
        var facade = CreateInProcessFacade();
        var executionId = Guid.CreateVersion7();
        var previousUser = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner")], "Test")
        );
        UserInfoUtil.Current = previousUser;
        try
        {
            var result = await facade.ExecuteAsync(
                await SeedAsync(CreateRequest(executionId, agentId, permissionMode: permissionMode)),
                cancellationToken
            );

            Assert.Equal(AgentExecutionState.Completed, result.State);
            Assert.Contains(
                result.Messages,
                message => message.Contents.OfType<AgwTextContent>().Any(content => content.Content == "done")
            );
            Assert.DoesNotContain(result.Messages, AgwMessageClassifier.IsTurnFinished);
            Assert.Equal(
                Agw.Shared.Data.Entities.Projects.ProjectConversationTurnStatus.Completed,
                (await _persistence.ReadTurnAsync(executionId)).Status
            );
            var execution = Assert.Single(_kit.Runtimes.TurnContexts);
            Assert.Equal("owner", execution.UserId);
            Assert.Equal(executionId, execution.TurnId);
            Assert.Equal(permissionMode == null ? null : AgwPermissionMode.FullAccess, execution.PermissionMode);
            Assert.Same(previousUser, UserInfoUtil.Current);
            Assert.True(Assert.Single(_kit.Runtimes.Created).IsDisposed);
        }
        finally
        {
            UserInfoUtil.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteStreamingAsync_InProcessApprovalWithoutPermission_FailsUnattendedExecution()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var agentId = await AddAgentAsync("owner", cancellationToken);
        _kit.Runtimes.RequestsApproval = true;
        var facade = CreateInProcessFacade();
        var events = new List<AgentExecutionEvent>();

        var exception = await Assert.ThrowsAsync<AgwException>(async () =>
        {
            await foreach (
                var item in facade.ExecuteStreamingAsync(
                    await SeedAsync(CreateRequest(Guid.CreateVersion7(), agentId, HumanInteractionPolicy.Reject)),
                    cancellationToken
                )
            )
            {
                events.Add(item);
            }
        });

        Assert.Equal(ErrorCodes.AgentExecutionFailed.Code, exception.Code);
        Assert.Contains("unattended", exception.Message, StringComparison.OrdinalIgnoreCase);
        var finished = Assert.Single(events, item => AgwMessageClassifier.IsTurnFinished(item.Message));
        Assert.Equal("failed", finished.Message.AdditionalProperties!["status"]);
        Assert.DoesNotContain(events, item => AgwMessageClassifier.IsTurnStart(item.Message));
    }

    [Fact]
    public async Task ExecuteAsync_InProcessAllowPolicy_StillUsesUnattendedRules()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var agentId = await AddAgentAsync("owner", cancellationToken);
        _kit.Runtimes.RequestsApproval = true;
        var facade = CreateInProcessFacade();

        var result = await facade.ExecuteAsync(
            await SeedAsync(
                CreateRequest(
                    Guid.CreateVersion7(),
                    agentId,
                    HumanInteractionPolicy.Allow,
                    AgentExecutionPermissionMode.FullAccess
                )
            ),
            cancellationToken
        );

        Assert.Equal(AgentExecutionState.Completed, result.State);
        Assert.Equal(2, _kit.Runtimes.TurnContexts.Count);
    }

    private AgentExecutionFacade CreateInProcessFacade() =>
        new(
            _persistence.CreateAcceptance(projectTasks: null, new WorkspaceProjects()),
            new AlwaysOwnedAgentCatalog(),
            _kit.CreateFactory(
                new Agw.Agents.Execution.Context.ExecutionContextFactory(_database.Context),
                _persistence
            )
        );

    /// <summary>
    /// 写入请求任务所属的项目与对话，受理事务按真实规则校验它们。
    /// Writes the project and conversation of the request's task so the acceptance transaction checks them by the real rules.
    /// </summary>
    private async Task<AgentExecutionRequest> SeedAsync(AgentExecutionRequest request)
    {
        await _persistence.SeedConversationAsync(
            new AgentExecutionTask
            {
                TaskId = request.Task.TaskId,
                ProjectId = request.Task.ProjectId,
                ProjectConversationId = request.Task.ProjectConversationId,
                ContextId = request.Task.ContextId,
            },
            request.OwnerUserId
        );
        return request;
    }

    private async Task<Guid> AddAgentAsync(string owner, CancellationToken cancellationToken)
    {
        var agentId = Guid.CreateVersion7();
        using (UserInfoUtil.PushSystemScope())
        {
            _database.Context.Agents.Add(
                new Agw.Shared.Data.Entities.Agents.Agent
                {
                    Id = agentId,
                    Name = $"agent-{agentId:N}",
                    CreateBy = owner,
                }
            );
            await _database.Context.SaveChangesAsync(cancellationToken);
        }
        return agentId;
    }

    private static AgentExecutionRequest CreateRequest(
        Guid executionId,
        Guid agentId,
        HumanInteractionPolicy policy = HumanInteractionPolicy.Allow,
        AgentExecutionPermissionMode? permissionMode = null
    ) =>
        new(
            executionId,
            "owner",
            new AgentTarget(AgentTargetKind.Agent, agentId),
            new ProjectTaskSnapshot(
                executionId,
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                "context",
                null,
                "Task",
                ProjectTaskStatus.Running,
                null,
                DateTimeOffset.UtcNow,
                null,
                null
            ),
            new AgwUserInput
            {
                MessageId = executionId.ToString("D"),
                Author = "user",
                Contents = [new AgwTextContent { Content = "run" }],
            },
            HumanInteractionPolicy: policy,
            PermissionMode: permissionMode
        );

    internal sealed class WorkspaceProjects : IProjectRuntimeFacade
    {
        public Task<ProjectRuntimeSnapshot?> GetForCurrentUserAsync(
            Guid projectId,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult<ProjectRuntimeSnapshot?>(
                new ProjectRuntimeSnapshot(
                    projectId,
                    "project",
                    AppContext.BaseDirectory,
                    null,
                    [],
                    new Dictionary<string, string>(),
                    [],
                    [],
                    []
                )
            );

        public Task<string?> GetWorkspaceAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(AppContext.BaseDirectory);
    }

    internal sealed class AlwaysOwnedAgentCatalog : IAgentCatalogFacade
    {
        public Task<IReadOnlyList<AgentDescriptor>> ListDiscoverableAsync(
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyList<AgentDescriptor>>([]);

        public Task<AgentDescriptor?> FindDiscoverableByNameAsync(
            string name,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<AgentDescriptor?>(null);

        public Task<IReadOnlySet<Guid>> FilterExistingMcpServerIdsAsync(
            IReadOnlyCollection<Guid> serverIds,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlySet<Guid>>(serverIds.ToHashSet());

        public Task<bool> IsOwnedTargetAsync(
            AgentRuntimeType type,
            Guid id,
            string ownerUserId,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(true);

        public Task<AgentCatalogMetrics> GetMetricsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentCatalogMetrics(0, 0));
    }
}
