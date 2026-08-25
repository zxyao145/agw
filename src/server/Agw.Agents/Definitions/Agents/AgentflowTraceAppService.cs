using Agw.Agents.Definitions.Contracts;
using Agw.Shared.Contracts.Pagination;
using Agw.Shared.Data.Entities.Agentflows;
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
        CancellationToken cancellationToken
    )
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

        var filteredQueryable = queryable;

        if (query.FromUtc.HasValue)
        {
            filteredQueryable = filteredQueryable.Where(trace => trace.StartTimeUtc >= query.FromUtc.Value);
        }

        if (query.ToUtc.HasValue)
        {
            filteredQueryable = filteredQueryable.Where(trace => trace.StartTimeUtc <= query.ToUtc.Value);
        }

        try
        {
            var total = await filteredQueryable.CountAsync(cancellationToken);
            var traces = await filteredQueryable
                .OrderByDescending(trace => trace.StartTimeUtc)
                .Skip((query.PageIndex - 1) * query.PageSize)
                .Take(query.PageSize)
                .ToListAsync(cancellationToken);

            return CreatePagedResult(query, traces, total);
        }
        catch (Exception exception) when (IsDateTimeOffsetQueryTranslationException(exception))
        {
            var traces = await queryable.ToListAsync(cancellationToken);
            IEnumerable<AgentflowTrace> filtered = traces;

            if (query.FromUtc.HasValue)
            {
                filtered = filtered.Where(trace => trace.StartTimeUtc >= query.FromUtc.Value);
            }

            if (query.ToUtc.HasValue)
            {
                filtered = filtered.Where(trace => trace.StartTimeUtc <= query.ToUtc.Value);
            }

            var ordered = filtered.OrderByDescending(trace => trace.StartTimeUtc).ToList();
            var page = ordered.Skip((query.PageIndex - 1) * query.PageSize).Take(query.PageSize).ToList();

            return CreatePagedResult(query, page, ordered.Count);
        }
    }

    private static PagedResult<AgentflowTraceDto> CreatePagedResult(
        AgentflowTraceQuery query,
        IReadOnlyList<AgentflowTrace> traces,
        int total
    )
    {
        return new PagedResult<AgentflowTraceDto>
        {
            Items = traces
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
                .ToList(),
            Total = total,
            PageIndex = query.PageIndex,
            PageSize = query.PageSize,
        };
    }

    private static bool IsDateTimeOffsetQueryTranslationException(Exception exception)
    {
        return exception is NotSupportedException
                && exception.Message.Contains(
                    "SQLite does not support expressions of type 'DateTimeOffset'",
                    StringComparison.Ordinal
                )
            || exception is InvalidOperationException
                && exception.Message.Contains("StartTimeUtc", StringComparison.Ordinal)
                && exception.Message.Contains("could not be translated", StringComparison.Ordinal);
    }

    private static void ValidateQuery(AgentflowTraceQuery query)
    {
        if (query.PageIndex < 1)
        {
            throw new AgwException(
                ErrorCodes.InvalidPageSize,
                $"Invalid pageIndex: {query.PageIndex}. Must be at least 1."
            );
        }

        if (query.PageSize < 1 || query.PageSize > MaxPageSize)
        {
            throw new AgwException(
                ErrorCodes.InvalidPageSize,
                $"Invalid pageSize: {query.PageSize}. Must be between 1 and {MaxPageSize}."
            );
        }

        if (query.FromUtc.HasValue && query.ToUtc.HasValue && query.FromUtc.Value > query.ToUtc.Value)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "fromUtc must be earlier than or equal to toUtc.");
        }
    }
}
