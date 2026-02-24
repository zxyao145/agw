using DSystem.Domain.Entities;
using DSystem.Domain.Repositories;

namespace DSystem.Domain.Services;

public class McpToolServerDomainService
{
    private readonly IRepository<McpToolServer> _repository;
    private readonly IUnitOfWork _unitOfWork;

    public McpToolServerDomainService(IRepository<McpToolServer> repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task<McpToolServer> CreateAsync(McpToolServer server, string user)
    {
        server.Id = server.Id == Guid.Empty ? Guid.NewGuid() : server.Id;
        server.CreateBy = user;
        server.CreateTime = DateTime.UtcNow;
        await _repository.AddAsync(server);
        await _unitOfWork.SaveChangesAsync();
        return server;
    }

    public async Task<McpToolServer?> UpdateAsync(Guid id, Action<McpToolServer> updateAction, string user)
    {
        var existing = await _repository.GetByIdAsync(id);
        if (existing == null)
        {
            return null;
        }

        updateAction(existing);
        existing.UpdateBy = user;
        existing.UpdateTime = DateTime.UtcNow;
        _repository.Update(existing);
        await _unitOfWork.SaveChangesAsync();
        return existing;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var existing = await _repository.GetByIdAsync(id);
        if (existing == null)
        {
            return false;
        }

        _repository.Remove(existing);
        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    public Task<IReadOnlyList<McpToolServer>> ListAsync() => _repository.ListAsync();

    public Task<McpToolServer?> GetAsync(Guid id) => _repository.GetByIdAsync(id);
}
