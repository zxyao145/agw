using System.Linq.Expressions;

using A2A;

using Agw.Agents.Application.AgentRun;
using Agw.Agents.Application.AgentRun.Dtos;
using Agw.Agents.Contracts;
using Agw.Agents.Domain.Entities;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Models;

using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.A2A.Tests;

public class A2AAgentRuntimeDependencyTests
{
    [Fact]
    public void AgentHandlerFactory_ServiceProviderValidation_HasSingleResolvableConstructor()
    {
        var services = new ServiceCollection()
            .AddScoped<A2AAgentService>(_ => new A2AAgentService(new InMemoryRepository<Agent>([])))
            .AddSingleton<IAgentExecutionBridge, FakeAgentExecutionBridge>()
            .AddSingleton<AgentHandlerFactory>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        Assert.NotNull(provider.GetRequiredService<AgentHandlerFactory>());
    }

    [Fact]
    public void A2AAgentService_Constructor_DoesNotDependOnConcreteAgentRuntimeService()
    {
        var constructor = Assert.Single(typeof(A2AAgentService).GetConstructors());

        Assert.DoesNotContain(
            constructor.GetParameters(),
            parameter => parameter.ParameterType == typeof(AgentRuntimeService));
    }

    [Fact]
    public async Task AgentExecutionBridge_ExecuteAsync_UsesAgentRuntimeServiceInterface()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            Name = "alpha",
            SystemPrompt = "Alpha prompt"
        };
        var runtimeService = new RecordingAgentRuntimeService();
        var services = new ServiceCollection()
            .AddSingleton<IRepository<Agent>>(new InMemoryRepository<Agent>([agent]))
            .AddSingleton<IAgentRuntimeService>(runtimeService)
            .BuildServiceProvider();
        var bridge = new AgentExecutionBridge(services.GetRequiredService<IServiceScopeFactory>());

        var result = await bridge.ExecuteAsync(
            "alpha",
            new RequestContext
            {
                TaskId = Guid.NewGuid().ToString("D"),
                ContextId = "ctx-a2a",
                StreamingResponse = false,
                Message = new Message
                {
                    Role = Role.User,
                    MessageId = "msg-user",
                    Parts = [Part.FromText("hello")]
                }
            },
            new AgwUserInput
            {
                MessageId = "msg-user",
                Author = "user",
                Contents = [new AgwTextContent { Content = "hello" }]
            },
            cancellationToken);

        Assert.NotNull(result);
        Assert.Equal(agent.Id, runtimeService.CapturedRequest?.AgentId);
    }

    private sealed class RecordingAgentRuntimeService : IAgentRuntimeService
    {
        public AgentExecuteByIdRequest? CapturedRequest { get; private set; }

        public Task<AIAgent?> CreateAiAgentAsync(Guid agentId, string? extraOverride = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<AgentExecSession?> CreateSessionAsync(
            Guid agentId,
            ProjectTask task,
            SettingCommand settings,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public async IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
            AgentExecSession session,
            AgwUserInput input,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<AgentExecutionResult?> ExecuteByNameAsync(AgentExecuteByNameRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<AgentExecutionResult?> ExecuteByIdAsync(AgentExecuteByIdRequest request, CancellationToken cancellationToken = default)
        {
            CapturedRequest = request;
            return Task.FromResult<AgentExecutionResult?>(new AgentExecutionResult(request.TaskId?.ToString("D") ?? string.Empty, []));
        }
    }

    private sealed class FakeAgentExecutionBridge : IAgentExecutionBridge
    {
        public Task<AgentExecutionResult?> ExecuteAsync(
            string agentName,
            RequestContext context,
            AgwUserInput input,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<AgentExecutionResult?>(new AgentExecutionResult(context.TaskId, []));
        }

        public async IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
            string agentName,
            RequestContext context,
            AgwUserInput input,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class InMemoryRepository<TEntity>(IEnumerable<TEntity> entities)
        : IRepository<TEntity>
        where TEntity : class
    {
        private readonly List<TEntity> _entities = entities.ToList();

        public IQueryable<TEntity> Queryable => _entities.AsQueryable();

        public Task<TEntity?> GetByIdAsync(object id)
        {
            throw new NotSupportedException();
        }

        public Task<TEntity?> SingleOrDefaultAsync(
            Expression<Func<TEntity, bool>> predicate,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_entities.AsQueryable().SingleOrDefault(predicate));
        }

        public Task<IReadOnlyList<TEntity>> ListAsync(Expression<Func<TEntity, bool>>? predicate = null)
        {
            return Task.FromResult((IReadOnlyList<TEntity>)ApplyQuery(predicate).ToList());
        }

        public Task<IReadOnlyList<TEntity>> ListAsync(Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>> orderBy)
        {
            return Task.FromResult((IReadOnlyList<TEntity>)orderBy(Queryable).ToList());
        }

        public Task<IReadOnlyList<TEntity>> ListAsync(
            Expression<Func<TEntity, bool>>? predicate,
            Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy)
        {
            var query = ApplyQuery(predicate);
            return Task.FromResult((IReadOnlyList<TEntity>)(orderBy is null ? query : orderBy(query)).ToList());
        }

        public Task<IReadOnlyList<TEntity>> ListAsync(
            Expression<Func<TEntity, bool>>? predicate = null,
            params Expression<Func<TEntity, object>>[] includes)
        {
            return Task.FromResult((IReadOnlyList<TEntity>)ApplyQuery(predicate).ToList());
        }

        public Task<IReadOnlyList<TEntity>> ListAsync(
            Expression<Func<TEntity, bool>>? predicate,
            Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy,
            params Expression<Func<TEntity, object>>[] includes)
        {
            var query = ApplyQuery(predicate);
            return Task.FromResult((IReadOnlyList<TEntity>)(orderBy is null ? query : orderBy(query)).ToList());
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

        private IQueryable<TEntity> ApplyQuery(Expression<Func<TEntity, bool>>? predicate)
        {
            return predicate is null ? Queryable : Queryable.Where(predicate);
        }
    }
}
