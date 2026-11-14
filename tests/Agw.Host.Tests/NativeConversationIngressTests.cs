using System.Security.Claims;
using A2A;
using Agw.A2A;
using Agw.A2A.Extensions;
using Agw.Agents.Contracts.Execution;
using Agw.Agents.Contracts.Messages;
using Agw.Agents.Execution.Commands.Exec;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Inbound.SignalR;
using Agw.Auth.Contracts;
using Agw.Infrastructure.Data;
using Agw.Projects.Contracts;
using Agw.Shared.Contracts.Conversations;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Agw.Host.Tests;

public sealed class NativeConversationIngressTests
{
    private const string UserId = "native-conversation-test";

    [Fact]
    public async Task A2ABridge_TaskSnapshotHoldsLock_ReusesNativeConversation()
    {
        var token = TestContext.Current.CancellationToken;
        await using var host = await ModuleCompositionTestHost.CreateAsync(configureServices: services =>
        {
            services.AddA2A(new Microsoft.Extensions.Configuration.ConfigurationManager());
        });
        using var user = EnterUser();
        var projectId = await CreateProjectAsync(
            host.Services,
            ProjectDefaults.A2AName,
            ProjectType.DefaultBuiltIn,
            token
        );
        var contextId = Guid.CreateVersion7().ToString("D");
        var taskId = Guid.CreateVersion7().ToString("D");
        var notifier = host.Services.GetRequiredService<AgwChannelEventNotifier>();
        var bridge = host.Services.GetRequiredService<IAgentExecutionBridge>();
        Task<Agw.A2A.AgentExecutionResult> execution;
        await using (await notifier.AcquireTaskLockAsync(taskId, token))
        {
            var message = new Message
            {
                Role = Role.User,
                MessageId = Guid.CreateVersion7().ToString("D"),
                Parts = [Part.FromText("Create a conversation")],
            };
            execution = bridge.ExecuteAsync(
                "missing-agent",
                new RequestContext
                {
                    TaskId = taskId,
                    ContextId = contextId,
                    StreamingResponse = false,
                    Message = message,
                },
                CreateInput(),
                token
            );
            Assert.False(execution.IsCompleted);
            await using var scope = host.Services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<ITaskStore>();
            await store.SaveTaskAsync(
                taskId,
                new AgentTask
                {
                    Id = taskId,
                    ContextId = contextId,
                    Status = new() { State = TaskState.Submitted },
                },
                token
            );
        }

        var failure = await Assert.ThrowsAsync<AgwException>(() => execution);
        Assert.Equal(ErrorCodes.AgentNotFound.Code, failure.Code);
        await using var readScope = host.Services.CreateAsyncScope();
        var db = readScope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var conversation = await db.ProjectConversations.SingleAsync(row => row.ProjectId == projectId, token);
        Assert.Equal(contextId, conversation.ContextId);
        Assert.Equal(ConversationKinds.Native, conversation.Kind);
        Assert.Null(conversation.RelateId);
    }

    [Fact]
    public async Task SignalRDispatch_FirstTurn_CreatesNativeConversation()
    {
        var token = TestContext.Current.CancellationToken;
        using var lifetimeHost = Microsoft
            .Extensions.Hosting.Host.CreateApplicationBuilder(
                new HostApplicationBuilderSettings { DisableDefaults = true }
            )
            .Build();
        var lifetime = lifetimeHost.Services.GetRequiredService<IHostApplicationLifetime>();
        await using var host = await ModuleCompositionTestHost.CreateAsync(
            addExecutionTransport: true,
            configureServices: services =>
            {
                services.AddSignalR();
                services.AddSingleton(lifetime);
            }
        );
        using var user = EnterUser();
        var projectId = await CreateProjectAsync(host.Services, "SignalR project", ProjectType.UserDefined, token);
        var conversationId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7().ToString("N");
        var connections = host.Services.GetRequiredService<ExecutionConnectionRegistry>();
        connections.Connect(connectionId, UserId);
        try
        {
            await connections.DispatchAsync(connectionId, UserId, new SettingCommand(projectId, conversationId), token);
            var command = new ExecCommand(AgentRuntimeType.Agent, CreateInput())
            {
                AgentId = Guid.CreateVersion7(),
                ConversationId = conversationId,
                TurnId = Guid.CreateVersion7(),
            };
            var failure = await Assert.ThrowsAsync<AgwException>(() =>
                connections.DispatchAsync(connectionId, UserId, command, token)
            );
            Assert.Equal(ErrorCodes.ResourceNotFound.Code, failure.Code);
        }
        finally
        {
            await connections.DisconnectAsync(connectionId);
        }

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var conversation = await db.ProjectConversations.SingleAsync(row => row.Id == conversationId, token);
        Assert.Equal(projectId, conversation.ProjectId);
        Assert.Equal(ConversationKinds.Native, conversation.Kind);
        Assert.Null(conversation.RelateId);
    }

    private static AgwUserInput CreateInput() =>
        new()
        {
            MessageId = Guid.CreateVersion7().ToString("D"),
            Author = "user",
            Contents = [new AgwTextContent { Content = "Create a conversation" }],
        };

    private static async Task<Guid> CreateProjectAsync(
        IServiceProvider services,
        string name,
        ProjectType type,
        CancellationToken token
    )
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var paths = services.GetRequiredService<Agw.Shared.Runtime.AgwDataPaths>();
        var project = new Project
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Type = type,
            Workspace = Path.Combine(paths.Root, "workspace"),
        };
        Directory.CreateDirectory(project.Workspace);
        db.Projects.Add(project);
        await db.SaveChangesAsync(token);
        return project.Id;
    }

    private static IDisposable EnterUser() =>
        UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId)], "Test"))
        );
}
