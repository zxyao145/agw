using Agw.Shared.Data.Entities.Projects;
using Microsoft.AspNetCore.Mvc;

namespace Agw.Projects.Contracts;

public sealed record ProviderSessionListQuery
{
    [FromQuery(Name = "projectId")]
    public Guid ProjectId { get; init; }

    [FromQuery(Name = "conversationId")]
    public Guid ConversationId { get; init; }
}

public sealed record ProviderSessionArchiveRequest(Guid ProjectId, Guid ConversationId, Guid BindingId);

/// <summary>
/// 一条 provider session 绑定记录；客户端按 (AgentId, ExternalAgentName) 分组，IsActive 为 true 的记录是该组当前生效的 session。
/// One provider session binding record; clients group by (AgentId, ExternalAgentName), and the record with IsActive true is the group's active session.
/// </summary>
public sealed record ProviderSessionResponse(
    Guid Id,
    Guid AgentId,
    string ExternalAgentName,
    string ProviderSessionId,
    bool IsActive,
    DateTimeOffset CreateTime,
    DateTimeOffset? UpdateTime
)
{
    public static ProviderSessionResponse FromDomain(ProjectConversationBinding binding) =>
        new(
            binding.Id,
            binding.AgentId,
            binding.ExternalAgentName,
            binding.ProviderSessionId,
            binding.IsActive,
            binding.CreateTime,
            binding.UpdateTime
        );
}
