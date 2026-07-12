using Agw.Agents.Definitions.Contracts;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;

using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Definitions.Agents;

public class AgentflowTraceAppService
{
    private const int MaxPageSize = 100;

    private readonly IRepository<AgentflowTrace> _traceRepository;

    public AgentflowTraceAppService(IRepository<AgentflowTrace> traceRepository)
    {
        _traceRepository = traceRepository;
    }

    public async Task<PagedResult<AgentflowTraceDto>> ListAsync(
        AgentflowTraceQuery query,
        CancellationToken cancellationToken)
    {
        ValidateQuery(query);

        var queryable = _traceRepository.Queryable.AsNoTracking();

        if (query.ProjectId.HasValue)
        {
            queryable = queryable.Where(trace => trace.ProjectId == query.ProjectId.Value);
        }

        if (!string.IsNullOrEmpty(query.ContextId))
        {
            queryable = queryable.Where(trace => trace.ContextId == query.ContextId);
        }

        if (query.AgentflowId.HasValue)
        {
            queryable = queryable.Where(trace => trace.AgentflowId == query.AgentflowId.Value);
        }

        if (query.FromUtc.HasValue)
        {
            queryable = queryable.Where(trace => trace.StartTimeUtc >= query.FromUtc.Value);
        }

        if (query.ToUtc.HasValue)
        {
            queryable = queryable.Where(trace => trace.StartTimeUtc <= query.ToUtc.Value);
        }

        var ordered = queryable.OrderByDescending(trace => trace.StartTimeUtc);
        var total = await ordered.CountAsync(cancellationToken);

        var items = await ordered
            .Skip((query.PageIndex - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(trace => new AgentflowTraceDto
            {
                Id = trace.Id,
                StartTimeUtc = trace.StartTimeUtc,
                ProjectId = trace.ProjectId,
                ContextId = trace.ContextId,
                TaskId = trace.TaskId,
                AgentflowId = trace.AgentflowId,
                NodeId = trace.NodeId,
                NodeName = trace.NodeName,
                NodeKind = trace.NodeKind,
                AgentId = trace.AgentId,
                AgentName = trace.AgentName,
                Input = trace.Input,
                DurationMilliseconds = trace.DurationMilliseconds,
                Status = trace.Status,
                Error = trace.Error,
            })
            .ToListAsync(cancellationToken);

        return new PagedResult<AgentflowTraceDto>
        {
            Items = items,
            Total = total,
            PageIndex = query.PageIndex,
            PageSize = query.PageSize,
        };
    }

    private static void ValidateQuery(AgentflowTraceQuery query)
    {
        if (query.PageIndex < 1)
        {
            throw new AgwException(
                ErrorCodes.InvalidPageSize,
                $"Invalid pageIndex: {query.PageIndex}. Must be at least 1.");
        }

        if (query.PageSize < 1 || query.PageSize > MaxPageSize)
        {
            throw new AgwException(
                ErrorCodes.InvalidPageSize,
                $"Invalid pageSize: {query.PageSize}. Must be between 1 and {MaxPageSize}.");
        }

        if (query.FromUtc.HasValue && query.ToUtc.HasValue && query.FromUtc.Value > query.ToUtc.Value)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "fromUtc must be earlier than or equal to toUtc.");
        }
    }
}
