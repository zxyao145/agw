using System.Security.Claims;
using Agw.Agents.Execution.Runtimes.Durable.Contracts;
using Agw.Projects.Contracts.Runtime;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Tests;

public partial class AgentflowTurnExecutorTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("default")]
    [InlineData("a2a")]
    [InlineData("ordinary")]
    public async Task ExecuteStreamingAsync_ProjectIdentifiers_ResolveOwnedProject(string identifier)
    {
        var defaults = new RecordingProjectDefaults();
        var projects = new RecordingRuntimeProjects();
        var fixture = CreateCharacterizationFixture(
            [AgentflowNodeKind.Agent, AgentflowNodeKind.Output],
            projectDefaults: defaults,
            projectRuntimeFacade: projects
        );
        Guid? projectId = identifier switch
        {
            "empty" => Guid.Empty,
            "default" => ProjectDefaults.DefaultBuiltInId,
            "a2a" => ProjectDefaults.A2AId,
            "ordinary" => Guid.CreateVersion7(),
            _ => null,
        };

        await CollectAsync(
            fixture.Service.ExecuteStreamingAsync(
                fixture.Flow.Id,
                "input",
                TestContext.Current.CancellationToken,
                projectId
            )
        );

        Assert.Equal(
            identifier == "ordinary" ? projectId
                : identifier == "a2a" ? defaults.A2AId
                : defaults.DefaultId,
            Assert.Single(projects.RequestedIds)
        );
        Assert.Equal(identifier is "null" or "empty" or "default" ? 1 : 0, defaults.DefaultCalls);
        Assert.Equal(identifier == "a2a" ? 1 : 0, defaults.A2ACalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execution_UnavailableProject_PreservesBoundarySpecificFailure(bool defaultMissing)
    {
        var defaults = new RecordingProjectDefaults(defaultMissing);
        var fixture = CreateCharacterizationFixture(
            [AgentflowNodeKind.Agent, AgentflowNodeKind.Output],
            projectDefaults: defaults,
            projectRuntimeFacade: defaultMissing ? new RecordingRuntimeProjects() : new MissingProjectRuntimeFacade()
        );
        var manifest = CreateManifest(fixture.Flow.Id);
        manifest = manifest with { Task = manifest.Task with { ProjectId = ProjectDefaults.DefaultBuiltInId } };

        var error = await Assert.ThrowsAsync<AgwException>(() =>
            CollectAsync(
                fixture.Service.ExecuteStreamingAsync(fixture.Flow.Id, "input", TestContext.Current.CancellationToken)
            )
        );
        var result = await fixture.Service.ExecuteDurableSegmentInScopeAsync(
            manifest,
            new(manifest.ExecutionId, 0, [], null),
            new RecordingSegmentSink(),
            TestContext.Current.CancellationToken
        );

        var unattendedError = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.ExecuteAsync(
                fixture.Flow.Id,
                Guid.CreateVersion7(),
                "input",
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(ErrorCodes.ResourceNotFound.Code, error.Code);
        Assert.Equal(ErrorCodes.ResourceNotFound.Code, unattendedError.Code);
        Assert.Equal(DurableExecutionSegmentStatus.Failed, result.Status);
        Assert.Equal(
            defaultMissing ? "The default project was not found." : "The project was not found.",
            result.ErrorMessage
        );
        Assert.Empty(fixture.Agents.CreatedAgents);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteStreamingAsync_UserContexts_RequireStableExecutionUser(bool invalidAuthentication)
    {
        var fixture = CreateCharacterizationFixture([AgentflowNodeKind.Agent, AgentflowNodeKind.Output]);
        using var invalidUser = invalidAuthentication
            ? UserInfoUtil.Push(new ClaimsPrincipal(new ClaimsIdentity([], "test")))
            : null;

        if (invalidAuthentication)
        {
            var exception = await Assert.ThrowsAsync<AgwException>(() =>
                CollectAsync(
                    fixture.Service.ExecuteStreamingAsync(
                        fixture.Flow.Id,
                        "input",
                        TestContext.Current.CancellationToken
                    )
                )
            );
            Assert.Equal(ErrorCodes.AuthenticationRequired.Code, exception.Code);
            Assert.Empty(fixture.Agents.CreatedAgents);
        }
        else
        {
            var messages = await CollectAsync(
                fixture.Service.ExecuteStreamingAsync(fixture.Flow.Id, "input", TestContext.Current.CancellationToken)
            );
            Assert.Equal(["input", "done", "done"], messages.Select(MessageShape));
            Assert.Single(fixture.Agents.CreatedAgents);
        }
    }

    private sealed class RecordingProjectDefaults : IProjectDefaultResolver
    {
        public Guid? DefaultId { get; }
        public Guid A2AId { get; } = Guid.CreateVersion7();
        public int DefaultCalls { get; private set; }
        public int A2ACalls { get; private set; }

        public RecordingProjectDefaults(bool missing = false)
        {
            DefaultId = missing ? null : Guid.CreateVersion7();
        }

        public Task<Guid?> ResolveDefaultProjectIdAsync(CancellationToken cancellationToken = default)
        {
            DefaultCalls++;
            return Task.FromResult(DefaultId);
        }

        public Task<Guid?> ResolveA2AProjectIdAsync(CancellationToken cancellationToken = default)
        {
            A2ACalls++;
            return Task.FromResult<Guid?>(A2AId);
        }
    }

    private sealed class RecordingRuntimeProjects : IProjectRuntimeFacade
    {
        public List<Guid> RequestedIds { get; } = [];

        public Task<ProjectRuntimeSnapshot?> GetForCurrentUserAsync(
            Guid projectId,
            CancellationToken cancellationToken = default
        )
        {
            RequestedIds.Add(projectId);
            return new TestProjectRuntimeFacade().GetForCurrentUserAsync(projectId, cancellationToken);
        }

        public Task<string?> GetWorkspaceAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }
}
