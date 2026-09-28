using Agw.Agents.Execution.Agents.History;
using Agw.Agents.Execution.Context;
using Agw.Agents.Execution.HumanInteraction.InProcess;
using Agw.Agents.Execution.Summaries;
using Agw.Agents.Execution.Turns;
using Agw.Projects.Application.History;
using Agw.Projects.Contracts.History;
using Agw.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Agents.Tests;

public sealed class AgentflowResultAttributionTests
{
    [Theory]
    [InlineData(ResultFormat.Markdown)]
    [InlineData(ResultFormat.Json)]
    public async Task CreateResultMessage_NodeScope_PreservesNodeNameInHistory(ResultFormat format)
    {
        using var user = TurnPersistenceTestKit.EnterUser();
        await using var kit = await TurnPersistenceTestKit.CreateAsync();
        var task = await kit.SeedConversationAsync();
        var context = ExecutionTestScopes.Context(
            projectId: task.ProjectId,
            contextId: task.ContextId,
            userId: TurnPersistenceTestKit.UserId,
            conversationId: task.ProjectConversationId,
            runtimeType: AgentRuntimeType.Agentflow
        );
        var root = ExecutionScope.Create(
            context,
            task.TaskId,
            new TurnBroadcast(context.TurnId, context.UserId, kit.Clock),
            new InMemoryPendingInteractionSet(null)
        );
        var writer = new ConversationHistoryWriter(
            kit.Services.GetRequiredService<IConversationHistoryStore>(),
            kit.Clock
        );

        using (root.Push())
        {
            foreach (var name in new[] { "commit", "push" })
            {
                using var node = root.CreateNodeScope(
                        new AgentflowNodeExecution(context.TurnTargetId, name, $" {name} ", 0, name),
                        Guid.CreateVersion7(),
                        EngineKind.Maf
                    )
                    .Push();
                var result = AgentTurnSummaryService.CreateResultMessage("{}", format, kit.Clock.GetUtcNow());
                Assert.Equal(name, result.AdditionalProperties!["nodeName"]);
                Assert.Equal(name, result.AdditionalProperties["interactionNodeId"]);
                Assert.Equal(Constants.DefaultAgentAuthor, result.AuthorName);
                await writer.AppendAsync(
                    task.ProjectId,
                    task.ContextId,
                    [result],
                    TestContext.Current.CancellationToken
                );
            }
            var workflowResult = AgentTurnSummaryService.CreateResultMessage("{}", format);
            Assert.False(workflowResult.AdditionalProperties!.ContainsKey("nodeName"));
        }

        var rows = await kit.ReadHistoryAsync(task.ProjectConversationId);
        Assert.Equal(
            ["commit", "push"],
            rows.Select(row => row.ToChatMessage()!.AdditionalProperties!["nodeName"]!.ToString())
        );
        Assert.Equal(
            ["commit", "push"],
            rows.Select(row => row.ToChatMessage()!.AdditionalProperties!["interactionNodeId"]!.ToString())
        );
        Assert.All(rows, row => Assert.Equal(Constants.DefaultAgentAuthor, row.ToChatMessage()!.AuthorName));
    }
}
