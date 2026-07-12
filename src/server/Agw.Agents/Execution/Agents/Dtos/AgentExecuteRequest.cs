using Agw.Shared.Data.Entities.Agents;

using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.Dtos;

public sealed class AgentExecuteByIdRequest
{
    public AgentExecuteByIdRequest(string input, Guid agentId, Guid? taskId, Guid? projectId, string? contextId)
    {
        var chatMsg = new ChatMessage(ChatRole.User, input)
        {
            AuthorName = Constants.DefaultInputAuthor
        };

        AgentId = agentId;
        TaskId = taskId;
        Input = [chatMsg];
        ProjectId = projectId;
        ContextId = contextId;
    }


    public AgentExecuteByIdRequest(List<ChatMessage> input, Guid agentId, Guid? taskId, Guid? projectId, string? contextId)
    {
        AgentId = agentId;
        TaskId = taskId;
        Input = input;
        ProjectId = projectId;
        ContextId = contextId;
    }

    public List<ChatMessage> Input { get; private set; }

    public Guid AgentId { get; private set; }

    public Guid? TaskId { get; private set; }

    public Guid? ProjectId { get; private set; }

    public string? ContextId { get; private set; }
}


public sealed class AgentExecuteRequest
{
    public required Agent Agent { get; init; }

    public Guid? TaskId { get; init; }
    public Guid? ProjectId { get; init; }
    public string? ContextId { get; init; }

    public required List<ChatMessage> Input { get; init; }

    public CancellationToken CancellationToken { get; init; } = default;
}
