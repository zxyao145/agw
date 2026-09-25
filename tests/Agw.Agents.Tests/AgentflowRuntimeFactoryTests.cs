using Agw.Agents.Execution.Agentflows.Runtime;
using Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public class AgentflowRuntimeFactoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateSessionScopeAsync_WithoutStore_PreservesExplicitConversationAndPermissionState(
        bool explicitConversation
    )
    {
        var factory = CreateFactory();
        var projectId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        Guid? conversationId = explicitConversation ? Guid.CreateVersion7() : null;
        var permissionState = new MafPermissionState(AgwPermissionMode.AlwaysAsk);

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
        var factory = CreateFactory(handoffProvider);
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

    private static AgentflowRuntimeFactory CreateFactory(IConversationHandoffProvider? handoffProvider = null) =>
        new(
            NullLogger<AgentflowRuntimeFactory>.Instance,
            definitions: null!,
            agentRuntimeFactory: null!,
            summaryService: null!,
            new ProviderState(),
            projectDefaults: null!,
            projects: null!,
            conversationHandoffProvider: handoffProvider
        );

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
