using System.Linq.Expressions;
using System.Runtime.CompilerServices;

using A2A;

using Agw.A2A.Extensions;
using Agw.Agents.Application.AgentRun.Dtos;
using Agw.Agents.Domain.Entities;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Models;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agw.A2A.Tests;

public class A2ARoutesBuilderExtensionsTests
{
    [Fact]
    public void MapAgwA2A_WithScopedRequestHandler_DoesNotResolveRequestHandlerAtStartup()
    {
        using var app = CreateApp();

        var exception = Record.Exception(() => app.MapAgwA2A("/api/a2a"));

        Assert.Null(exception);
    }

    [Fact]
    public void MapAgwA2A_WithBasePrefix_MapsOneAgentNameSegment()
    {
        using var app = CreateApp();

        app.MapAgwA2A("/api/a2a");

        var routePatterns = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToArray();

        Assert.Contains("/api/a2a/{agentName}", routePatterns);
        Assert.Contains("/api/a2a/{agentName}/.well-known/agent-card.json", routePatterns);
        Assert.DoesNotContain(routePatterns, route => route?.Contains("{agentName}{agentName}", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(routePatterns, route => route?.Contains("{agentName}/.well-known/{agentName}", StringComparison.Ordinal) == true);
    }

    private static WebApplication CreateApp()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });

        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });

        builder.Services.AddLogging();
        builder.Services.AddRouting();
        builder.Services.AddSingleton<IRepository<Agent>, EmptyAgentRepository>();
        builder.Services.AddScoped<A2AAgentService>();
        builder.Services.AddSingleton<IAgentExecutionBridge, FakeAgentExecutionBridge>();
        builder.Services.AddSingleton<AgentHandlerFactory>();
        builder.Services.AddScoped<IAgwA2ARequestHandler, ThrowingRequestHandler>();

        return builder.Build();
    }

    private sealed class ThrowingRequestHandler : IAgwA2ARequestHandler
    {
        public Task<SendMessageResponse> SendMessageAsync(string agentName, SendMessageRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(string agentName, SendMessageRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<AgentTask> GetTaskAsync(GetTaskRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ListTasksResponse> ListTasksAsync(string agentName, ListTasksRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentTask> CancelTaskAsync(string agentName, CancelTaskRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(CreateTaskPushNotificationConfigRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ListTaskPushNotificationConfigResponse> ListTaskPushNotificationConfigAsync(ListTaskPushNotificationConfigRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
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
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class EmptyAgentRepository : IRepository<Agent>
    {
        public IQueryable<Agent> Queryable => Enumerable.Empty<Agent>().AsQueryable();

        public Task<Agent?> GetByIdAsync(object id) =>
            Task.FromResult<Agent?>(null);

        public Task<Agent?> SingleOrDefaultAsync(Expression<Func<Agent, bool>> predicate, CancellationToken cancellationToken = default) =>
            Task.FromResult<Agent?>(null);

        public Task<IReadOnlyList<Agent>> ListAsync(Expression<Func<Agent, bool>>? predicate = null) =>
            Task.FromResult((IReadOnlyList<Agent>)[]);

        public Task<IReadOnlyList<Agent>> ListAsync(Func<IQueryable<Agent>, IOrderedQueryable<Agent>> orderBy) =>
            Task.FromResult((IReadOnlyList<Agent>)[]);

        public Task<IReadOnlyList<Agent>> ListAsync(
            Expression<Func<Agent, bool>>? predicate,
            Func<IQueryable<Agent>, IOrderedQueryable<Agent>>? orderBy) =>
            Task.FromResult((IReadOnlyList<Agent>)[]);

        public Task<IReadOnlyList<Agent>> ListAsync(
            Expression<Func<Agent, bool>>? predicate = null,
            params Expression<Func<Agent, object>>[] includes) =>
            Task.FromResult((IReadOnlyList<Agent>)[]);

        public Task<IReadOnlyList<Agent>> ListAsync(
            Expression<Func<Agent, bool>>? predicate,
            Func<IQueryable<Agent>, IOrderedQueryable<Agent>>? orderBy,
            params Expression<Func<Agent, object>>[] includes) =>
            Task.FromResult((IReadOnlyList<Agent>)[]);

        public Task AddAsync(Agent entity) =>
            Task.CompletedTask;

        public void Update(Agent entity)
        {
        }

        public void Remove(Agent entity)
        {
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
