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

    public async Task<AgentSessionRecord?> GetBySessionIdAsync(string sessionId, Guid? projectId = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        if (projectId.HasValue)
        {
            var byProject = await _repository.ListAsync(r => r.SessionId == sessionId && r.ProjectId == projectId.Value);
            var exact = byProject.FirstOrDefault();
            if (exact != null)
            {
                return exact;
            }
        }

        var matches = await _repository.ListAsync(r => r.SessionId == sessionId);
        return matches.OrderByDescending(r => r.UpdateTime ?? r.CreateTime).FirstOrDefault();
    }

    public async Task<bool> DeleteBySessionIdAsync(string sessionId, Guid? projectId = null)
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

    public async Task<bool> UpdateTitleAsync(string sessionId, Guid? projectId, string title, string user)
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
