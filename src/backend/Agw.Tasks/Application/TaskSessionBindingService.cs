using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;

using Microsoft.EntityFrameworkCore;

namespace Agw.Tasks.Application;

public class TaskSessionBindingService : ITaskSessionBindingService
{
    private readonly IRepository<TaskSessionBinding> _bindingRepository;
    private readonly IUnitOfWork _unitOfWork;

    public TaskSessionBindingService(
        IRepository<TaskSessionBinding> bindingRepository,
        IUnitOfWork unitOfWork)
    {
        _bindingRepository = bindingRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<TaskSessionBinding?> GetAsync(
        Guid taskId,
        Guid agentId,
        string externalAgentName,
        CancellationToken cancellationToken = default)
    {
        var normalizedAgentName = NormalizeExternalAgentName(externalAgentName);
        if (string.IsNullOrWhiteSpace(normalizedAgentName))
        {
            return null;
        }

        return await _bindingRepository.Queryable
            .AsNoTracking()
            .SingleOrDefaultAsync(
                binding => binding.TaskId == taskId
                    && binding.AgentId == agentId
                    && binding.ExternalAgentName == normalizedAgentName,
                cancellationToken);
    }

    public async Task<TaskSessionBinding> UpsertAsync(
        Guid taskId,
        Guid agentId,
        string externalAgentName,
        string providerSessionId,
        string user,
        CancellationToken cancellationToken = default)
    {
        var normalizedAgentName = NormalizeExternalAgentName(externalAgentName);
        var normalizedProviderSessionId = NormalizeProviderSessionId(providerSessionId);
        var normalizedUser = string.IsNullOrWhiteSpace(user) ? "system" : user.Trim();
        var now = DateTime.UtcNow;

        var binding = await _bindingRepository.Queryable
            .SingleOrDefaultAsync(
                existing => existing.TaskId == taskId
                    && existing.AgentId == agentId
                    && existing.ExternalAgentName == normalizedAgentName,
                cancellationToken);

        if (binding == null)
        {
            binding = new TaskSessionBinding
            {
                Id = Guid.NewGuid(),
                TaskId = taskId,
                AgentId = agentId,
                ExternalAgentName = normalizedAgentName,
                ProviderSessionId = normalizedProviderSessionId,
                CreateBy = normalizedUser,
                CreateTime = now
            };
            await _bindingRepository.AddAsync(binding);
        }
        else if (binding.ProviderSessionId != normalizedProviderSessionId)
        {
            binding.ProviderSessionId = normalizedProviderSessionId;
            binding.UpdateBy = normalizedUser;
            binding.UpdateTime = now;
            _bindingRepository.Update(binding);
        }

        await _unitOfWork.SaveChangesAsync();
        return binding;
    }

    private static string NormalizeExternalAgentName(string externalAgentName)
    {
        if (string.IsNullOrWhiteSpace(externalAgentName))
        {
            return string.Empty;
        }

        return externalAgentName.Trim().ToLowerInvariant();
    }

    private static string NormalizeProviderSessionId(string providerSessionId)
    {
        if (string.IsNullOrWhiteSpace(providerSessionId))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Provider session id is required.");
        }

        return providerSessionId.Trim();
    }
}
