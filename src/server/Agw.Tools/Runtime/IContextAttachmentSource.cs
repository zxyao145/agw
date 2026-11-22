using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Tools.Runtime;

public interface IContextAttachmentSource
{
    ValueTask<ContextAttachment?> EvaluateAsync(
        AgentSession session,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken
    );
}
