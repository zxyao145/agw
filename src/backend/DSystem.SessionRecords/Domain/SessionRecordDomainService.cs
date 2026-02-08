using DSystem.Domain.Repositories;
using DSystem.SessionRecords.Entities;
using System.Linq.Expressions;

namespace DSystem.SessionRecords.Domain;

public class SessionRecordDomainService
{
    private readonly IRepository<AgentSessionRecord> _repository;
    private readonly IUnitOfWork _unitOfWork;

    public SessionRecordDomainService(
        IRepository<AgentSessionRecord> repository,
        IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public Task<IReadOnlyList<AgentSessionRecord>> ListAsync(Expression<Func<AgentSessionRecord, bool>>? predicate = null) =>
        _repository.ListAsync(predicate);

    public async Task<AgentSessionRecord?> GetBySessionIdAsync(string sessionId, Guid projectId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var matches = await _repository.ListAsync(r => r.SessionId == sessionId && r.ProjectId == projectId);
        return matches.FirstOrDefault();
    }

    public async Task<bool> DeleteBySessionIdAsync(string sessionId, Guid projectId)
    {
        var record = await GetBySessionIdAsync(sessionId, projectId);
        if (record == null)
        {
            return false;
        }

        _repository.Remove(record);
        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    public async Task<bool> UpdateTitleAsync(string sessionId, Guid projectId, string title, string user)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var record = await GetBySessionIdAsync(sessionId, projectId);
        if (record == null)
        {
            return false;
        }

        record.Title = title.Trim();
        record.UpdateBy = user;
        record.UpdateTime = DateTime.UtcNow;
        _repository.Update(record);
        await _unitOfWork.SaveChangesAsync();
        return true;
    }
}
