using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using A2A;
using Agw.Agents.Contracts.Catalog;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.A2A.Tests;

public class AgentHandlerFactoryTests
{
    [Fact]
    public async Task CreateAsync_SameAgentName_ReturnsSameHandlerInstance()
    {
        var factory = CreateFactory(
            new Agent
            {
                Id = Guid.CreateVersion7(),
                Name = "alpha",
                SystemPrompt = "Alpha prompt",
            }
        );

        var first = await factory.CreateAsync("alpha");
        var second = await factory.CreateAsync("alpha");

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.IsType<CommonAgentHandler>(first);
    }

    [Fact]
    public async Task CreateAsync_DifferentAgentNames_ReturnsDifferentHandlerInstances()
    {
        var factory = CreateFactory(
            new Agent
            {
                Id = Guid.CreateVersion7(),
                Name = "alpha",
                SystemPrompt = "Alpha prompt",
            },
            new Agent
            {
                Id = Guid.CreateVersion7(),
                Name = "beta",
                SystemPrompt = "Beta prompt",
            }
        );

        var alpha = await factory.CreateAsync("alpha");
        var beta = await factory.CreateAsync("beta");

        Assert.NotNull(alpha);
        Assert.NotNull(beta);
        Assert.NotSame(alpha, beta);
    }

    [Fact]
    public async Task CreateAsync_MissingAgentThenAdded_ReturnsCreatedHandler()
    {
        var repository = new InMemoryRepository<Agent>();
        var factory = CreateFactory(repository);

        var missing = await factory.CreateAsync("alpha");

        await repository.AddAsync(
            new Agent
            {
                Id = Guid.CreateVersion7(),
                Name = "alpha",
                SystemPrompt = "Alpha prompt",
            }
        );
        var created = await factory.CreateAsync("alpha");

        Assert.Null(missing);
        Assert.NotNull(created);
        Assert.IsType<CommonAgentHandler>(created);
    }

    [Fact]
    public async Task CommonAgentHandlerCreateAsync_WhenAgentExists_ReturnsHandlerWithAgentCard()
    {
        var repository = new InMemoryRepository<Agent>([
            new Agent
            {
                Id = Guid.CreateVersion7(),
                Name = "alpha",
                SystemPrompt = "Alpha prompt",
            },
        ]);
        var factory = CreateFactory(repository);
        var handler = await factory.CreateAsync("alpha");

        var commonHandler = Assert.IsType<CommonAgentHandler>(handler);
        Assert.Equal("alpha", (await commonHandler.GetAgentCardAsync())!.Name);
    }

    [Fact]
    public void CommonAgentHandler_GetAgentCardAsync_IsInstanceMethod()
    {
        var method = typeof(CommonAgentHandler).GetMethod(
            "GetAgentCardAsync",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
        );

        Assert.NotNull(method);
        Assert.False(method!.IsStatic);
    }

    [Fact]
    public void CommonAgentHandler_Constructor_DoesNotAcceptAgentCard()
    {
        var constructors = typeof(CommonAgentHandler).GetConstructors();

        Assert.DoesNotContain(
            constructors,
            constructor => constructor.GetParameters().Any(parameter => parameter.ParameterType == typeof(AgentCard))
        );
        Assert.Contains(
            constructors,
            constructor =>
            {
                var parameters = constructor.GetParameters();
                return parameters.Length >= 2
                    && parameters[0].ParameterType == typeof(string)
                    && parameters[1].ParameterType == typeof(IAgentExecutionBridge);
            }
        );
    }

    [Fact]
    public async Task ExecuteAsync_WhenStreamingResponseIsFalse_UsesNonStreamingExecutionAndCompletes()
    {
        var data = JsonDocument.Parse("""{"kind":"json"}""").RootElement.Clone();
        var bridge = new FakeAgentExecutionBridge
        {
            ExecuteAsyncImpl = (_, context, input, _) =>
            {
                return Task.FromResult(
                    new AgentExecutionResult(context.TaskId, context.ContextId, [CreateTextMessage("final answer")])
                );
            },
        };
        var handler = CreateHandler("alpha", bridge);
        var queue = new AgentEventQueue(capacity: 32);

        await handler.ExecuteAsync(
            CreateRequestContext(
                streamingResponse: false,
                parts:
                [
                    Part.FromText("hello"),
                    Part.FromUrl("https://example.com/result", "text/html", "result.html"),
                    Part.FromData(data),
                    Part.FromRaw([0x01, 0x02, 0x03], "application/octet-stream", "payload.bin"),
                ]
            ),
            queue,
            TestContext.Current.CancellationToken
        );
        queue.Complete(exception: null);

        var events = await DrainAsync(queue, TestContext.Current.CancellationToken);

        Assert.True(bridge.ExecuteCalled);
        Assert.False(bridge.ExecuteStreamingCalled);
        Assert.NotNull(bridge.CapturedInput);
        Assert.Collection(
            bridge.CapturedInput!.Contents,
            content => Assert.Equal("hello", Assert.IsType<AgwTextContent>(content).Content),
            content => Assert.Equal("https://example.com/result", Assert.IsType<AgwUriContent>(content).Uri.ToString()),
            content => Assert.Equal("""{"kind":"json"}""", Assert.IsType<AgwTextContent>(content).Content),
            content => Assert.Equal("AQID", Assert.IsType<AgwTextContent>(content).Content)
        );
        Assert.Contains(
            events,
            response => response.ArtifactUpdate?.Artifact?.Parts?.Any(part => part.Text == "final answer") == true
        );
        Assert.Contains(events, response => response.StatusUpdate?.Status?.State == TaskState.Completed);
    }

    [Fact]
    public async Task ExecuteAsync_WhenStreamingResponseIsTrue_UsesStreamingExecutionAndSkipsTurnFinishedMarker()
    {
        var bridge = new FakeAgentExecutionBridge
        {
            ExecuteStreamingAsyncImpl = (_, _, _, _) =>
                ToAsyncEnumerable(CreateTextMessage("chunk-1"), CreateTurnFinishedMessage()),
        };
        var handler = CreateHandler("alpha", bridge);
        var queue = new AgentEventQueue(capacity: 32);

        await handler.ExecuteAsync(
            CreateRequestContext(streamingResponse: true, parts: [Part.FromText("hello")]),
            queue,
            TestContext.Current.CancellationToken
        );
        queue.Complete(exception: null);

        var events = await DrainAsync(queue, TestContext.Current.CancellationToken);

        Assert.True(bridge.ExecuteStreamingCalled);
        Assert.False(bridge.ExecuteCalled);
        Assert.Contains(events, response => response.StatusUpdate?.Status?.State == TaskState.Working);
        Assert.Contains(
            events,
            response => response.ArtifactUpdate?.Artifact?.Parts?.Any(part => part.Text == "chunk-1") == true
        );
        Assert.DoesNotContain(
            events,
            response => response.ArtifactUpdate?.Artifact?.Parts?.Any(part => string.IsNullOrEmpty(part.Text)) == true
        );
        Assert.Contains(events, response => response.StatusUpdate?.Status?.State == TaskState.Completed);
    }

    [Fact]
    public async Task ExecuteAsync_WhenExecutionThrows_FailsTask()
    {
        var bridge = new FakeAgentExecutionBridge
        {
            ExecuteAsyncImpl = (_, _, _, _) => throw new InvalidOperationException("boom"),
        };
        var handler = CreateHandler("alpha", bridge);
        var queue = new AgentEventQueue(capacity: 32);

        await handler.ExecuteAsync(
            CreateRequestContext(streamingResponse: false, parts: [Part.FromText("hello")]),
            queue,
            TestContext.Current.CancellationToken
        );
        queue.Complete(exception: null);

        var events = await DrainAsync(queue, TestContext.Current.CancellationToken);

        Assert.Contains(events, response => response.StatusUpdate?.Status?.State == TaskState.Failed);
        Assert.Contains(
            events,
            response => response.StatusUpdate?.Status?.Message?.Parts?.Any(part => part.Text == "boom") == true
        );
    }

    [Fact]
    public async Task ExecuteAsync_WhenTurnIsInterrupted_CancelsWithoutCompleting()
    {
        var bridge = new FakeAgentExecutionBridge
        {
            ExecuteStreamingAsyncImpl = (_, _, _, _) =>
                ToAsyncEnumerable(CreateTextMessage("partial"), CreateTurnFinishedMessage("interrupted")),
        };
        var handler = CreateHandler("alpha", bridge);
        var queue = new AgentEventQueue(capacity: 32);

        await handler.ExecuteAsync(
            CreateRequestContext(streamingResponse: true, parts: [Part.FromText("hello")]),
            queue,
            TestContext.Current.CancellationToken
        );

        var events = await DrainAsync(queue, TestContext.Current.CancellationToken);

        Assert.Contains(events, response => response.StatusUpdate?.Status?.State == TaskState.Canceled);
        Assert.DoesNotContain(events, response => response.StatusUpdate?.Status?.State == TaskState.Completed);
        Assert.DoesNotContain(events, response => response.StatusUpdate?.Status?.State == TaskState.Failed);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTurnReportsFailure_FailsWithoutCompleting()
    {
        var bridge = new FakeAgentExecutionBridge
        {
            ExecuteStreamingAsyncImpl = (_, _, _, _) => ToAsyncEnumerable(CreateTurnFinishedMessage("failed")),
        };
        var handler = CreateHandler("alpha", bridge);
        var queue = new AgentEventQueue(capacity: 32);

        await handler.ExecuteAsync(
            CreateRequestContext(streamingResponse: true, parts: [Part.FromText("hello")]),
            queue,
            TestContext.Current.CancellationToken
        );

        var events = await DrainAsync(queue, TestContext.Current.CancellationToken);

        Assert.Contains(events, response => response.StatusUpdate?.Status?.State == TaskState.Failed);
        Assert.DoesNotContain(events, response => response.StatusUpdate?.Status?.State == TaskState.Completed);
    }

    [Fact]
    public async Task ListTasksAsync_WhenAgentDoesNotExist_ThrowsAgwInvalidAgentResponse()
    {
        var requestHandler = CreateRequestHandler(CreateFactory(new InMemoryRepository<Agent>()));

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            requestHandler.ListTasksAsync(
                "missing-agent",
                new ListTasksRequest(),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(ErrorCodes.A2AInvalidAgentResponse.Code, exception.Code);
    }

    [Fact]
    public async Task CancelTaskAsync_WhenDurableInterruptLosesTerminalRace_DoesNotWriteCanceled()
    {
        var taskId = Guid.CreateVersion7().ToString("D");
        var taskStore = new FakeTaskStore(
            new AgentTask
            {
                Id = taskId,
                ContextId = "ctx-cancel",
                Status = new global::A2A.TaskStatus
                {
                    State = TaskState.Working,
                    Timestamp = TimeProvider.System.GetUtcNow(),
                },
            }
        );
        var durableBridge = new FakeDurableA2AExecutionBridge(interrupted: false);
        var services = new ServiceCollection()
            .AddSingleton<IDurableA2AExecutionBridge>(durableBridge)
            .BuildServiceProvider();
        var handler = new AgwA2ARequestHandler(
            taskStore,
            new AgwChannelEventNotifier(),
            NullLogger<A2AServer>.Instance,
            services.GetRequiredService<IServiceScopeFactory>()
        );

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            handler.CancelTaskAsync(
                "alpha",
                new CancelTaskRequest { Id = taskId },
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(ErrorCodes.A2ATaskNotCancelable.Code, exception.Code);
        Assert.True(durableBridge.CancelCalled);
        Assert.Equal(0, taskStore.SaveCount);
    }

    [Fact]
    public async Task CancelTaskAsync_InProcessBridge_UsesNonDurableFallback()
    {
        var taskId = Guid.CreateVersion7().ToString("D");
        var taskStore = new FakeTaskStore(
            new AgentTask
            {
                Id = taskId,
                ContextId = "ctx-cancel",
                Status = new global::A2A.TaskStatus
                {
                    State = TaskState.Working,
                    Timestamp = TimeProvider.System.GetUtcNow(),
                },
            }
        );
        var durableBridge = new FakeDurableA2AExecutionBridge(interrupted: false, supportsDurableOperations: false);
        var services = new ServiceCollection()
            .AddSingleton<IDurableA2AExecutionBridge>(durableBridge)
            .AddSingleton(
                CreateFactory(
                    new Agent
                    {
                        Id = Guid.CreateVersion7(),
                        Name = "alpha",
                        SystemPrompt = "Alpha prompt",
                    }
                )
            )
            .BuildServiceProvider();
        var handler = new AgwA2ARequestHandler(
            taskStore,
            new AgwChannelEventNotifier(),
            NullLogger<A2AServer>.Instance,
            services.GetRequiredService<IServiceScopeFactory>()
        );

        var canceledTask = await handler.CancelTaskAsync(
            "alpha",
            new CancelTaskRequest { Id = taskId },
            TestContext.Current.CancellationToken
        );

        Assert.False(durableBridge.CancelCalled);
        Assert.Equal(TaskState.Canceled, canceledTask.Status.State);
        Assert.Equal(1, taskStore.SaveCount);
    }

    [Fact]
    public async Task SubscribeToTaskAsync_InProcessBridge_UsesChannelFallback()
    {
        var taskId = Guid.CreateVersion7().ToString("D");
        var currentTask = new AgentTask
        {
            Id = taskId,
            ContextId = "ctx-subscribe",
            Status = new global::A2A.TaskStatus
            {
                State = TaskState.Working,
                Timestamp = TimeProvider.System.GetUtcNow(),
            },
        };
        var taskStore = new FakeTaskStore(currentTask);
        var durableBridge = new FakeDurableA2AExecutionBridge(interrupted: false, supportsDurableOperations: false);
        var services = new ServiceCollection()
            .AddSingleton<IDurableA2AExecutionBridge>(durableBridge)
            .BuildServiceProvider();
        var notifier = new AgwChannelEventNotifier();
        var handler = new AgwA2ARequestHandler(
            taskStore,
            notifier,
            NullLogger<A2AServer>.Instance,
            services.GetRequiredService<IServiceScopeFactory>()
        );
        await using var subscription = handler
            .SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = taskId }, TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await subscription.MoveNextAsync());
        Assert.Same(currentTask, subscription.Current.Task);

        var terminalEvent = new StreamResponse
        {
            StatusUpdate = new TaskStatusUpdateEvent
            {
                TaskId = taskId,
                ContextId = currentTask.ContextId,
                Status = new global::A2A.TaskStatus
                {
                    State = TaskState.Canceled,
                    Timestamp = TimeProvider.System.GetUtcNow(),
                },
            },
        };
        using (await notifier.AcquireTaskLockAsync(taskId, TestContext.Current.CancellationToken))
        {
            notifier.Notify(taskId, terminalEvent);
        }

        Assert.True(await subscription.MoveNextAsync());
        Assert.Same(terminalEvent, subscription.Current);
        Assert.False(await subscription.MoveNextAsync());
        Assert.False(durableBridge.SubscribeCalled);
    }

    [Fact]
    public async Task ApplyEventAsync_WhenTerminalTransitionConflicts_PreservesFirstTerminalState()
    {
        var taskId = Guid.CreateVersion7().ToString("D");
        var currentTask = new AgentTask
        {
            Id = taskId,
            ContextId = "ctx-terminal",
            Status = new global::A2A.TaskStatus
            {
                State = TaskState.Canceled,
                Timestamp = TimeProvider.System.GetUtcNow(),
            },
        };
        var taskStore = new FakeTaskStore(currentTask);
        var services = new ServiceCollection().BuildServiceProvider();
        var handler = new AgwA2ARequestHandler(
            taskStore,
            new AgwChannelEventNotifier(),
            NullLogger<A2AServer>.Instance,
            services.GetRequiredService<IServiceScopeFactory>()
        );
        var method = typeof(AgwA2ARequestHandler).GetMethod(
            "ApplyEventAsync",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        var response = new StreamResponse
        {
            StatusUpdate = new TaskStatusUpdateEvent
            {
                TaskId = taskId,
                ContextId = currentTask.ContextId,
                Status = new global::A2A.TaskStatus
                {
                    State = TaskState.Completed,
                    Timestamp = TimeProvider.System.GetUtcNow(),
                },
            },
        };
        var context = new RequestContext
        {
            TaskId = taskId,
            ContextId = currentTask.ContextId,
            Task = currentTask,
            StreamingResponse = false,
            Message = new Message
            {
                Role = Role.User,
                MessageId = "message",
                Parts = [Part.FromText("hello")],
            },
        };

        var invocation = method!.Invoke(handler, [response, context, TestContext.Current.CancellationToken]);
        var accepted = await Assert.IsType<Task<bool>>(invocation);

        Assert.False(accepted);
        Assert.Equal(0, taskStore.SaveCount);
        Assert.Equal(TaskState.Canceled, taskStore.CurrentTask?.Status.State);
    }

    private static AgentHandlerFactory CreateFactory(params Agent[] agents)
    {
        return CreateFactory(new InMemoryRepository<Agent>(agents));
    }

    private static AgentHandlerFactory CreateFactory(InMemoryRepository<Agent> repository)
    {
        var service = CreateA2AAgentService(repository);

        return new AgentHandlerFactory(service, new FakeAgentExecutionBridge());
    }

    private static CommonAgentHandler CreateHandler(string agentName, IAgentExecutionBridge executionBridge)
    {
        return new CommonAgentHandler(
            agentName,
            executionBridge,
            CreateA2AAgentService(
                new InMemoryRepository<Agent>([
                    new Agent
                    {
                        Id = Guid.CreateVersion7(),
                        Name = agentName,
                        SystemPrompt = $"{agentName} prompt",
                    },
                ])
            )
        );
    }

    private static A2AAgentService CreateA2AAgentService(InMemoryRepository<Agent> repository)
    {
        return new A2AAgentService(new RepositoryAgentCatalog(repository));
    }

    private static AgwA2ARequestHandler CreateRequestHandler(AgentHandlerFactory agentHandlerFactory)
    {
        var services = new ServiceCollection().AddSingleton(agentHandlerFactory).BuildServiceProvider();

        return new AgwA2ARequestHandler(
            new FakeTaskStore(),
            new AgwChannelEventNotifier(),
            NullLogger<A2AServer>.Instance,
            services.GetRequiredService<IServiceScopeFactory>()
        );
    }

    private static AgentCard CreateAgentCard(string name) =>
        new()
        {
            Name = name,
            Description = $"{name} description",
            Version = "1.0.0",
            Capabilities = new AgentCapabilities { Streaming = true },
        };

    private static RequestContext CreateRequestContext(bool streamingResponse, List<Part> parts) =>
        new()
        {
            TaskId = Guid.CreateVersion7().ToString("D"),
            ContextId = "ctx-a2a",
            StreamingResponse = streamingResponse,
            Message = new Message
            {
                Role = Role.User,
                MessageId = "msg-user",
                ContextId = "ctx-a2a",
                Parts = parts,
            },
        };

    private static AgwMessage CreateTextMessage(string text) =>
        new(Guid.CreateVersion7().ToString("N"), "$agent", AiRole.System, [new AgwTextContent { Content = text }]);

    private static AgwMessage CreateTurnFinishedMessage(string? status = null) =>
        new(
            Guid.CreateVersion7().ToString("N"),
            "$agw",
            AiRole.System,
            [new AgwTextContent { Content = string.Empty }],
            status == null
                ? new AdditionalPropertiesDictionary { ["type"] = "turn-finished" }
                : new AdditionalPropertiesDictionary { ["type"] = "turn-finished", ["status"] = status }
        );

    private static async Task<List<StreamResponse>> DrainAsync(
        AgentEventQueue queue,
        CancellationToken cancellationToken
    )
    {
        var responses = new List<StreamResponse>();
        await foreach (var response in queue.WithCancellation(cancellationToken))
        {
            responses.Add(response);
        }

        return responses;
    }

    private static async IAsyncEnumerable<AgwMessage> ToAsyncEnumerable(params AgwMessage[] messages)
    {
        foreach (var message in messages)
        {
            yield return message;
            await Task.Yield();
        }
    }

    private sealed class InMemoryRepository<TEntity>(IEnumerable<TEntity>? entities = null) : IRepository<TEntity>
        where TEntity : class
    {
        private readonly List<TEntity> _entities = entities?.ToList() ?? [];

        public IQueryable<TEntity> Queryable => _entities.AsQueryable();

        public Task<TEntity?> GetByIdAsync(object id)
        {
            throw new NotSupportedException();
        }

        public Task<TEntity?> SingleOrDefaultAsync(
            Expression<Func<TEntity, bool>> predicate,
            CancellationToken cancellationToken = default
        )
        {
            return Task.FromResult(_entities.AsQueryable().SingleOrDefault(predicate));
        }

        public Task<IReadOnlyList<TEntity>> ListAsync(Expression<Func<TEntity, bool>>? predicate = null)
        {
            return Task.FromResult((IReadOnlyList<TEntity>)ApplyPredicate(predicate));
        }

        public Task<IReadOnlyList<TEntity>> ListAsync(Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>> orderBy)
        {
            return Task.FromResult((IReadOnlyList<TEntity>)orderBy(ApplyQuery(predicate: null)).ToList());
        }

        public Task<IReadOnlyList<TEntity>> ListAsync(
            Expression<Func<TEntity, bool>>? predicate,
            Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy
        )
        {
            var query = ApplyQuery(predicate);
            var results = orderBy is null ? query.ToList() : orderBy(query).ToList();
            return Task.FromResult((IReadOnlyList<TEntity>)results);
        }

        public Task<IReadOnlyList<TEntity>> ListAsync(
            Expression<Func<TEntity, bool>>? predicate = null,
            params Expression<Func<TEntity, object>>[] includes
        )
        {
            return Task.FromResult((IReadOnlyList<TEntity>)ApplyPredicate(predicate));
        }

        public Task<IReadOnlyList<TEntity>> ListAsync(
            Expression<Func<TEntity, bool>>? predicate,
            Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy,
            params Expression<Func<TEntity, object>>[] includes
        )
        {
            var query = ApplyQuery(predicate);
            var results = orderBy is null ? query.ToList() : orderBy(query).ToList();
            return Task.FromResult((IReadOnlyList<TEntity>)results);
        }

        public Task AddAsync(TEntity entity)
        {
            _entities.Add(entity);
            return Task.CompletedTask;
        }

        public void Update(TEntity entity)
        {
            throw new NotSupportedException();
        }

        public void Remove(TEntity entity)
        {
            _entities.Remove(entity);
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        private List<TEntity> ApplyPredicate(Expression<Func<TEntity, bool>>? predicate)
        {
            return [.. ApplyQuery(predicate)];
        }

        private IQueryable<TEntity> ApplyQuery(Expression<Func<TEntity, bool>>? predicate)
        {
            return predicate is null ? _entities.AsQueryable() : _entities.AsQueryable().Where(predicate);
        }
    }

    private sealed class FakeAgentExecutionBridge : IAgentExecutionBridge
    {
        public bool ExecuteCalled { get; private set; }

        public bool ExecuteStreamingCalled { get; private set; }

        public RequestContext? CapturedContext { get; set; }

        public AgwUserInput? CapturedInput { get; set; }

        public Func<
            string,
            RequestContext,
            AgwUserInput,
            CancellationToken,
            Task<AgentExecutionResult>
        > ExecuteAsyncImpl { get; set; } =
            (_, context, _, _) => Task.FromResult(new AgentExecutionResult(context.TaskId, context.ContextId, []));

        public Func<
            string,
            RequestContext,
            AgwUserInput,
            CancellationToken,
            IAsyncEnumerable<AgwMessage>
        > ExecuteStreamingAsyncImpl { get; set; } = (_, _, _, _) => ToAsyncEnumerable();

        public async Task<AgentExecutionResult> ExecuteAsync(
            string agentName,
            RequestContext context,
            AgwUserInput input,
            CancellationToken cancellationToken
        )
        {
            ExecuteCalled = true;
            CapturedContext = context;
            CapturedInput = input;
            return await ExecuteAsyncImpl(agentName, context, input, cancellationToken);
        }

        public IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
            string agentName,
            RequestContext context,
            AgwUserInput input,
            CancellationToken cancellationToken
        )
        {
            ExecuteStreamingCalled = true;
            CapturedContext = context;
            CapturedInput = input;
            return ExecuteStreamingAsyncImpl(agentName, context, input, cancellationToken);
        }
    }

    private sealed class RepositoryAgentCatalog : IAgentCatalogFacade
    {
        private readonly InMemoryRepository<Agent> _repository;

        public RepositoryAgentCatalog(InMemoryRepository<Agent> repository)
        {
            _repository = repository;
        }

        public Task<IReadOnlyList<AgentDescriptor>> ListDiscoverableAsync(
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult<IReadOnlyList<AgentDescriptor>>(
                _repository
                    .Queryable.Select(agent => new AgentDescriptor(
                        agent.Id,
                        agent.Name,
                        agent.DisplayName,
                        agent.SystemPrompt
                    ))
                    .ToArray()
            );

        public async Task<AgentDescriptor?> FindDiscoverableByNameAsync(
            string name,
            CancellationToken cancellationToken = default
        ) => (await ListDiscoverableAsync(cancellationToken)).SingleOrDefault(agent => agent.Name == name);

        public Task<IReadOnlySet<Guid>> FilterExistingMcpServerIdsAsync(
            IReadOnlyCollection<Guid> serverIds,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

        public Task<AgentCatalogMetrics> GetMetricsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentCatalogMetrics(_repository.Queryable.Count(), 0));
    }

    private sealed class FakeTaskStore : ITaskStore
    {
        private AgentTask? _task;

        public FakeTaskStore(AgentTask? task = null)
        {
            _task = task;
        }

        public int SaveCount { get; private set; }
        public AgentTask? CurrentTask => _task;

        public Task DeleteTaskAsync(string taskId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<AgentTask?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_task);
        }

        public Task<ListTasksResponse> ListTasksAsync(
            ListTasksRequest request,
            CancellationToken cancellationToken = default
        )
        {
            return Task.FromResult(new ListTasksResponse { Tasks = [] });
        }

        public Task SaveTaskAsync(string taskId, AgentTask task, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            _task = task;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDurableA2AExecutionBridge(bool interrupted, bool supportsDurableOperations = true)
        : IDurableA2AExecutionBridge
    {
        public bool CancelCalled { get; private set; }

        public bool SubscribeCalled { get; private set; }

        public bool SupportsDurableOperations { get; } = supportsDurableOperations;

        public Task<AgentExecutionResult> ExecuteAsync(
            string agentName,
            RequestContext context,
            AgwUserInput input,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
            string agentName,
            RequestContext context,
            AgwUserInput input,
            CancellationToken cancellationToken
        ) => ToAsyncEnumerable();

        public IAsyncEnumerable<AgwMessage> SubscribeAsync(
            string taskId,
            string? cursor,
            CancellationToken cancellationToken
        )
        {
            SubscribeCalled = true;
            return ToAsyncEnumerable();
        }

        public Task<bool> CancelAsync(string taskId, CancellationToken cancellationToken)
        {
            CancelCalled = true;
            return Task.FromResult(interrupted);
        }
    }
}
