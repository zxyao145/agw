using System.Runtime.CompilerServices;
using A2A;
using Agw.A2A.Extensions;
using Agw.Agents.Contracts.Catalog;
using Agw.Auth.Contracts;
using Agw.Projects.Contracts.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.A2A.Tests;

public class A2ADependencyInjectionTests
{
    [Fact]
    public void AddA2A_WithFacadeRegistrations_BuildsServiceProvider()
    {
        var services = CreateA2AServices();

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );

        Assert.NotNull(provider.GetRequiredService<AgentHandlerFactory>());
    }

    [Fact]
    public async Task AgentExecutionBridge_FacadeRegistered_ExecutesAgent()
    {
        var execution = new FakeAgentExecutionFacade();
        var services = CreateA2AServices(execution);
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
        var taskId = Guid.CreateVersion7();

        var result = await provider
            .GetRequiredService<IAgentExecutionBridge>()
            .ExecuteAsync(
                "alpha",
                new RequestContext
                {
                    TaskId = taskId.ToString("D"),
                    ContextId = "ctx-a2a",
                    StreamingResponse = false,
                    Message = new Message
                    {
                        Role = Role.User,
                        MessageId = "msg-user",
                        ContextId = "ctx-a2a",
                        Parts = [Part.FromText("hello")],
                    },
                },
                new AgwUserInput
                {
                    MessageId = "msg-user",
                    Author = "user",
                    Contents = [new AgwTextContent { Content = "hello" }],
                },
                TestContext.Current.CancellationToken
            );

        Assert.NotNull(result);
        Assert.NotNull(execution.Request);
        Assert.Equal("alpha", execution.Request!.Target.Name);
        Assert.Equal(taskId, execution.Request.ExecutionId);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("InProcess", false)]
    [InlineData("Distributed", true)]
    public void AddA2A_AnyExecutionProvider_RegistersTopologyNeutralBridge(
        string? executionProvider,
        bool supportsDurableOperations
    )
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationManager();
        configuration["Execution:Provider"] = executionProvider;

        services.AddA2A(configuration);
        using var provider = services.BuildServiceProvider();

        var executionBridge = provider.GetRequiredService<IAgentExecutionBridge>();
        var durableBridge = provider.GetRequiredService<IDurableA2AExecutionBridge>();
        Assert.Same(executionBridge, durableBridge);
        Assert.Equal(supportsDurableOperations, durableBridge.SupportsDurableOperations);
    }

    private static ServiceCollection CreateA2AServices(FakeAgentExecutionFacade? execution = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IAgentCatalogFacade, FakeAgentCatalogFacade>();
        services.AddScoped<IAgentExecutionFacade>(_ => execution ?? new FakeAgentExecutionFacade());
        services.AddScoped<IDurableAgentExecutionFacade, FakeDurableAgentExecutionFacade>();
        services.AddScoped<IProjectTaskFacade, FakeProjectTaskFacade>();
        services.AddScoped<IExternalTaskSnapshotStore, FakeExternalTaskSnapshotStore>();
        services.AddScoped<IUserInfoService, FakeUserInfoService>();
        services.AddA2A(new ConfigurationManager());
        return services;
    }

    private sealed class FakeAgentExecutionFacade : IAgentExecutionFacade
    {
        public Agw.Agents.Contracts.Execution.AgentExecutionRequest? Request { get; private set; }

        public Task<Agw.Agents.Contracts.Execution.AgentExecutionResult> ExecuteAsync(
            Agw.Agents.Contracts.Execution.AgentExecutionRequest request,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult(
                new Agw.Agents.Contracts.Execution.AgentExecutionResult(
                    request.ExecutionId,
                    AgentExecutionState.Completed,
                    []
                )
            );

        public async IAsyncEnumerable<AgentExecutionEvent> ExecuteStreamingAsync(
            Agw.Agents.Contracts.Execution.AgentExecutionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            Request = request;
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FakeDurableAgentExecutionFacade : IDurableAgentExecutionFacade
    {
        public Task<Agw.Agents.Contracts.Execution.AgentExecutionResult> GetOutcomeAsync(
            Guid executionId,
            string ownerUserId,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult(
                new Agw.Agents.Contracts.Execution.AgentExecutionResult(executionId, AgentExecutionState.Completed, [])
            );

        public async IAsyncEnumerable<AgentExecutionEvent> SubscribeAsync(
            Guid executionId,
            string ownerUserId,
            string? afterCursor,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<bool> InterruptAsync(
            Guid executionId,
            string ownerUserId,
            string reason,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(true);
    }

    private sealed class FakeProjectTaskFacade : IProjectTaskFacade
    {
        public Task<int?> GetGenerationAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(0);

        public Task<ProjectTaskSnapshot> ResolveAsync(
            ResolveProjectTaskRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<ProjectTaskSnapshot?> GetAsync(Guid taskId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ProjectTaskSnapshot?>(null);

        public Task<ProjectTaskSnapshot> GetOrCreateAsync(
            StartProjectTaskRequest request,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult(
                new ProjectTaskSnapshot(
                    request.TaskId,
                    Guid.CreateVersion7(),
                    request.ProjectId,
                    request.ContextId ?? request.TaskId.ToString("D"),
                    request.JobId,
                    request.Title ?? "A2A Task",
                    request.InitialStatus,
                    null,
                    DateTimeOffset.UtcNow,
                    null,
                    null
                )
            );

        public Task<ProjectTaskSnapshot?> FinishAsync(
            FinishProjectTaskRequest request,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<ProjectTaskSnapshot?>(null);

        public Task<IReadOnlyDictionary<Guid, string?>> ResolveContextIdsAsync(
            IReadOnlyCollection<Guid> taskIds,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyDictionary<Guid, string?>>(new Dictionary<Guid, string?>());
    }

    private sealed class FakeExternalTaskSnapshotStore : IExternalTaskSnapshotStore
    {
        public Task<ExternalTaskSnapshot?> GetAsync(
            Guid projectId,
            Guid taskId,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<ExternalTaskSnapshot?>(null);

        public Task<IReadOnlyList<ExternalTaskSnapshot>> ListAsync(
            Guid projectId,
            string? contextId,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyList<ExternalTaskSnapshot>>([]);

        public Task<ExternalTaskSaveResult> SaveAsync(
            SaveExternalTaskSnapshotRequest request,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(ExternalTaskSaveResult.Saved);

        public Task DeleteAsync(Guid projectId, Guid taskId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeAgentCatalogFacade : IAgentCatalogFacade
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
        ) => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

        public Task<AgentCatalogMetrics> GetMetricsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentCatalogMetrics(0, 0));

        public Task<bool> IsOwnedTargetAsync(
            Agw.Agents.Contracts.Execution.AgentRuntimeType type,
            Guid id,
            string ownerUserId,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(true);
    }

    private sealed class FakeUserInfoService : IUserInfoService
    {
        public System.Security.Claims.ClaimsPrincipal? Current { get; set; }
        public string? UserId => "test-user";
        public bool IsAuthenticated => true;
        public string RequiredUserId => "test-user";
    }
}
