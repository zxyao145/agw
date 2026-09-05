using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Agents.Store;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Turns;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

public class AgentflowExecutionContextFactoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateSessionScopeAsync_WithoutStore_PreservesExplicitConversationAndPermissionState(
        bool explicitConversation
    )
    {
        var factory = new AgentflowExecutionContextFactory(new ProviderState());
        var projectId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        Guid? conversationId = explicitConversation ? Guid.CreateVersion7() : null;
        var permissionState = new PermissionModeState(PermissionMode.AlwaysAsk);

        var scope = await factory.CreateSessionScopeAsync(
            projectId,
            " context ",
            taskId,
            conversationId,
            TestContext.Current.CancellationToken,
            permissionState
        );

        Assert.Equal(projectId, scope.ProjectId);
        Assert.Equal(taskId, scope.TaskId);
        Assert.Equal("context", scope.ContextId);
        Assert.Equal(conversationId ?? Guid.Empty, scope.ConversationId);
        Assert.Same(permissionState, scope.PermissionState);
    }

    [Fact]
    public async Task CreateWorkflowInputMessagesAsync_LoadsHandoffAndPreservesInputIdentity()
    {
        var handoffProvider = new HandoffProvider();
        var factory = new AgentflowExecutionContextFactory(
            new ProviderState(),
            conversationHandoffProvider: handoffProvider
        );
        var flowId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        var input = new AgwUserInput
        {
            MessageId = "input-id",
            Author = "user",
            Contents = [new AgwTextContent { Content = "request" }],
        };

        var messages = await factory.CreateWorkflowInputMessagesAsync(
            flowId,
            conversationId,
            input,
            TestContext.Current.CancellationToken
        );

        Assert.Equal((conversationId, AgentRuntimeType.Agentflow, flowId), handoffProvider.LastRequest);
        Assert.Equal(["handoff", "request"], messages.Select(message => message.Text));
        Assert.Equal("input-id", messages[1].MessageId);
        Assert.Equal("user", messages[1].AuthorName);
        Assert.Equal(12L, messages[1].AdditionalProperties![ConversationHandoffMetadata.ThroughSequenceKey]);
        Assert.Equal(flowId.ToString("D"), messages[1].Contents[0].AdditionalProperties!["targetId"]);
        Assert.Null(input.Contents[0].AdditionalProperties);
    }

    [Fact]
    public void WorkflowFactory_DoesNotDependOnSessionOrInputPreparationServices()
    {
        var parameters = Assert
            .Single(typeof(AgentflowWorkflowFactory).GetConstructors())
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.DoesNotContain(typeof(IProviderSessionState), parameters);
        Assert.DoesNotContain(typeof(AgentSessionStateStore), parameters);
        Assert.DoesNotContain(typeof(IConversationHistoryWriter), parameters);
        Assert.DoesNotContain(typeof(IConversationHandoffProvider), parameters);
        Assert.Equal(7, parameters.Length);
    }

    private sealed class ProviderState : IProviderSessionState
    {
        public void InitializeSessionState(AgentSession session, string contextId, Guid projectId) { }

        public void InitializeSessionState(
            AgentSession session,
            string contextId,
            Guid projectId,
            string historyScope
        ) { }

        public bool TryGetProjectContext(AgentSession session, out Guid projectId, out string contextId)
        {
            projectId = default;
            contextId = "";
            return false;
        }
    }

    private sealed class HandoffProvider : IConversationHandoffProvider
    {
        public (Guid ConversationId, AgentRuntimeType Type, Guid TargetId)? LastRequest { get; private set; }

        public Task<ConversationHandoff> CreateAsync(
            Guid conversationId,
            AgentRuntimeType targetType,
            Guid targetId,
            CancellationToken cancellationToken = default
        )
        {
            LastRequest = (conversationId, targetType, targetId);
            return Task.FromResult(new ConversationHandoff([new ChatMessage(ChatRole.System, "handoff")], 12));
        }
    }
}
