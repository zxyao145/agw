using DSystem.SessionRecords.Entities;
using DSystem.SessionRecords.Repositories;
using System.Linq.Expressions;

namespace DSystem.SessionRecords.Domain;

public class SessionRecordDomainService
{
    private readonly IAgentSessionRecordRepository _repository;
    private readonly ISessionRecordsUnitOfWork _unitOfWork;

    public SessionRecordDomainService(
        IAgentSessionRecordRepository repository,
        ISessionRecordsUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public Task<IReadOnlyList<AgentSessionRecord>> ListAsync(Expression<Func<AgentSessionRecord, bool>>? predicate = null) =>
        _repository.ListAsync(predicate);

    public async Task<IReadOnlyList<AgentSessionRecord>> GetBySessionIdAsync(string sessionId, string projectId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return [];
        }

        var byProject = await _repository.ListAsync(r => r.SessionId == sessionId && r.ProjectId == projectId);
        return byProject.OrderByDescending(r => r.UpdateTime ?? r.CreateTime).ToList();
    }

    public async Task<bool> DeleteBySessionIdAsync(string sessionId, string projectId)
    {
        var records = await GetBySessionIdAsync(sessionId, projectId);
        if (records.Count == 0)
        {
            return false;
        }

        foreach (var record in records)
        {
            _repository.Remove(record);
        }

        await _unitOfWork.SaveChangesAsync();
        return true;
    }
}
