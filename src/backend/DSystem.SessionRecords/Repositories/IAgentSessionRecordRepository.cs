using DSystem.SessionRecords.Entities;
using System.Linq.Expressions;

namespace DSystem.SessionRecords.Repositories;

public interface IAgentSessionRecordRepository
{
    Task<IReadOnlyList<AgentSessionRecord>> ListAsync(Expression<Func<AgentSessionRecord, bool>>? predicate = null);

    Task AddAsync(AgentSessionRecord entity);

    void Update(AgentSessionRecord entity);

    void Remove(AgentSessionRecord entity);
}
