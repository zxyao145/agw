using System.Linq.Expressions;
using System.Runtime.CompilerServices;

using A2A;

using Agw.A2A.Extensions;
using Agw.Agents.Application.AgentRun;
using Agw.Agents.Application.AgentRun.Dtos;
using Agw.Agents.Contracts;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Shared.Data.Repositories;

using Microsoft.Agents.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.A2A.Tests;

public class A2ADependencyInjectionTests
{
    [Fact]
    public void AddA2A_WithOnlyAgentRuntimeInterfaceRegistration_BuildsServiceProvider()
    {
        var services = CreateA2AServices();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        Assert.NotNull(provider.GetRequiredService<AgentHandlerFactory>());
    }

    [Fact]
    public async Task AgentExecutionBridge_WithOnlyAgentRuntimeInterfaceRegistration_ExecutesAgent()
    {
        var agentId = Guid.NewGuid();
        var runtime = new FakeAgentRuntimeService();
        var services = new ServiceCollection();
        services.AddScoped<IAgentRuntimeService>(_ => runtime);
        services.AddScoped<IRepository<Agent>>(_ => new RepositoryStub<Agent>(
        [
            new Agent { Id = agentId, Name = "alpha", SystemPrompt = "Alpha prompt" }
        ]));
        services.AddSingleton<IAgentExecutionBridge, A2AAgentExecutionBridge>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        var result = await provider.GetRequiredService<IAgentExecutionBridge>().ExecuteAsync(
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
                    ContextId = "ctx-a2a",
                    Parts = [Part.FromText("hello")]
                }
            },
            new AgwUserInput
            {
                MessageId = "msg-user",
                Author = "user",
                Contents = [new AgwTextContent { Content = "hello" }]
            },
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.NotNull(runtime.CapturedRequest);
        Assert.Equal(agentId, runtime.CapturedRequest!.AgentId);
    }

    private static ServiceCollection CreateA2AServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IAgentRuntimeService, FakeAgentRuntimeService>();
        services.AddScoped<IRepository<Agent>, RepositoryStub<Agent>>();
        services.AddScoped<IRepository<ProjectTask>, RepositoryStub<ProjectTask>>();
        services.AddScoped<IRepository<TaskRecord>, RepositoryStub<TaskRecord>>();
        services.AddScoped<IUnitOfWork, UnitOfWorkStub>();
        services.AddA2A(new ConfigurationManager());
        return services;
    }

    private sealed class FakeAgentRuntimeService : IAgentRuntimeService
    {
        public AgentExecuteByIdRequest? CapturedRequest { get; private set; }

        public Task<AIAgent?> CreateAiAgentAsync(
            Guid agentId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AIAgent?>(null);

        public Task<AgentExecSession?> CreateSessionAsync(
            Guid agentId,
            ProjectTask task,
            SettingCommand settings,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AgentExecSession?>(null);

        public async IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
            AgentExecSession session,
            AgwUserInput input,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<AgentExecutionResult?> ExecuteByNameAsync(
            AgentExecuteByNameRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AgentExecutionResult?>(null);

        public Task<AgentExecutionResult?> ExecuteByIdAsync(
            AgentExecuteByIdRequest request,
            CancellationToken cancellationToken = default)
        {
            CapturedRequest = request;
            return Task.FromResult<AgentExecutionResult?>(new AgentExecutionResult(
                request.TaskId?.ToString("D") ?? Guid.NewGuid().ToString("D"),
                []));
        }
    }

    private sealed class RepositoryStub<TEntity>(IEnumerable<TEntity>? entities = null) : IRepository<TEntity>
        where TEntity : class
    {
        private readonly List<TEntity> _entities = entities?.ToList() ?? [];

        public IQueryable<TEntity> Queryable => _entities.AsQueryable();

        public Task<TEntity?> GetByIdAsync(object id) =>
            Task.FromResult<TEntity?>(null);

        public Task<TEntity?> SingleOrDefaultAsync(
            Expression<Func<TEntity, bool>> predicate,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_entities.AsQueryable().SingleOrDefault(predicate));

        public Task<IReadOnlyList<TEntity>> ListAsync(
            Expression<Func<TEntity, bool>>? predicate = null,
            Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null)
        {
            var query = ApplyPredicate(predicate);
            var results = orderBy is null ? query.ToList() : orderBy(query).ToList();
            return Task.FromResult((IReadOnlyList<TEntity>)results);
        }

        public Task<IReadOnlyList<TEntity>> ListAsync(
            Expression<Func<TEntity, bool>>? predicate = null,
            Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null,
            params Expression<Func<TEntity, object>>[] includes) =>
            ListAsync(predicate, orderBy);

        public Task AddAsync(TEntity entity)
        {
            _entities.Add(entity);
            return Task.CompletedTask;
        }

        public void Update(TEntity entity)
        {
        }

        public void Remove(TEntity entity)
        {
            _entities.Remove(entity);
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        private IQueryable<TEntity> ApplyPredicate(Expression<Func<TEntity, bool>>? predicate) =>
            predicate is null ? _entities.AsQueryable() : _entities.AsQueryable().Where(predicate);
    }

    private sealed class UnitOfWorkStub : IUnitOfWork
    {
        public Task<int> SaveChangesAsync() =>
            Task.FromResult(0);
    }
}
