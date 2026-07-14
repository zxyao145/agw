using Microsoft.Agents.AI.Workflows;

using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agw.Agents.Execution.Agentflows.Builders;

internal static class ConcurrentBlockBuilder
{
    /// <summary>
    /// 构建并发执行全部参与者并汇总其响应消息的 Block 执行器。
    /// </summary>
    internal static ExecutorBinding? Build(AgentflowBlockBuildContext context)
    {
        var participants = AgentflowBlockBuildSupport.ResolveParticipants(context);
        if (participants == null)
        {
            return null;
        }

        Func<List<ChatMessage>, CancellationToken, ValueTask<List<ChatMessage>>> runConcurrentAsync =
            async (messages, cancellationToken) =>
            {
                var input = AgentflowMessageTransforms.ApplyInstructions(
                    messages,
                    context.BlockNode.Instructions);
                var tasks = participants
                    .Select(participant => participant.Agent.RunAsync(
                        AgentflowMessageTransforms.ReassignOtherAgentsAsUsers(
                            input,
                            participant.Agent.Name ?? participant.Agent.Id),
                        cancellationToken: cancellationToken))
                    .ToArray();
                var responses = await Task.WhenAll(tasks).ConfigureAwait(false);
                return responses.SelectMany(response => response.Messages).ToList();
            };

        return runConcurrentAsync.BindAsExecutor<List<ChatMessage>, List<ChatMessage>>(
            context.BlockNode.NodeId,
            ExecutorOptions.Default,
            threadsafe: true);
    }
}
