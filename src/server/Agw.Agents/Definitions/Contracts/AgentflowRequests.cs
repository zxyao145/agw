using System.Text.Json.Serialization;
using Agw.Agents.Definitions.Domain;

namespace Agw.Agents.Definitions.Contracts;

public sealed record AgentflowDto
{
    [JsonConstructor]
    public AgentflowDto(
        Guid id,
        string name,
        string? description,
        bool enable,
        int latestRevision,
        int publishedRevision,
        DateTimeOffset createTime,
        DateTimeOffset updateTime
    )
    {
        Id = id;
        Name = name;
        Description = description;
        Enable = enable;
        LatestRevision = latestRevision;
        PublishedRevision = publishedRevision;
        CreateTime = createTime;
        UpdateTime = updateTime;
    }

    public Guid Id { get; init; }

    public string Name { get; init; }

    public string? Description { get; init; }

    public bool Enable { get; init; }

    public int LatestRevision { get; init; }

    public int PublishedRevision { get; init; }

    public DateTimeOffset CreateTime { get; init; }

    public DateTimeOffset UpdateTime { get; init; }
}

public sealed record AgentflowCreateRequest(string Name, string? Description, AgentflowDefinitionDto Definition);

public sealed record AgentflowSaveRevisionRequest
{
    [JsonConstructor]
    public AgentflowSaveRevisionRequest(
        Guid agentflowId,
        int baseRevision,
        string name,
        string? description,
        AgentflowDefinitionDto definition
    )
    {
        AgentflowId = agentflowId;
        BaseRevision = baseRevision;
        Name = name;
        Description = description;
        Definition = definition;
    }

    public Guid AgentflowId { get; init; }

    public int BaseRevision { get; init; }

    public string Name { get; init; }

    public string? Description { get; init; }

    public AgentflowDefinitionDto Definition { get; init; }
}

public sealed record AgentflowPublishRequest(Guid AgentflowId, int Revision);

public sealed record AgentflowValidateRequest(Guid? AgentflowId, string Name, AgentflowDefinitionDto Definition);

public sealed record AgentflowValidateResponse(IReadOnlyList<AgentflowProblem> Problems);

public sealed record AgentflowDetailResponse(AgentflowDto Agentflow, int Revision, AgentflowDefinitionDto Definition);

public sealed record AgentflowRevisionDto(int Revision, DateTimeOffset CreateTime);

public sealed record AgentflowEnabledUpdateRequest(Guid AgentflowId, bool Enable);
