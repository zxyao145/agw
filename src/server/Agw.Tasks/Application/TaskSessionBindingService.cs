using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;

using Microsoft.EntityFrameworkCore;

namespace Agw.Tasks.Application;

public class TaskSessionBindingService : ITaskSessionBindingService
{
    private readonly IRepository<TaskSessionBinding> _bindingRepository;
    private readonly IRepository<ProjectContext> _contextRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public TaskSessionBindingService(
        IRepository<TaskSessionBinding> bindingRepository,
        IRepository<ProjectContext> contextRepository,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _bindingRepository = bindingRepository;
        _contextRepository = contextRepository;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task<TaskSessionBinding?> GetAsync(
        Guid projectId,
        string contextId,
        Guid agentId,
        string externalAgentName,
        CancellationToken cancellationToken = default)
    {
        var normalizedAgentName = NormalizeExternalAgentName(externalAgentName);
        var normalizedContextId = NormalizeContextId(contextId);
        if (string.IsNullOrWhiteSpace(normalizedAgentName) || string.IsNullOrWhiteSpace(normalizedContextId))
        {
            return null;
        }

        var projectContext = await _contextRepository.Queryable
            .AsNoTracking()
            .SingleOrDefaultAsync(
                context => context.ProjectId == projectId && context.ContextId == normalizedContextId,
                cancellationToken);

        if (projectContext == null)
        {
            return null;
        }

        return await _bindingRepository.Queryable
            .AsNoTracking()
            .SingleOrDefaultAsync(
                binding => binding.ProjectContextId == projectContext.Id
                    && binding.AgentId == agentId
                    && binding.ExternalAgentName == normalizedAgentName,
                cancellationToken);
    }

    public async Task<TaskSessionBinding> UpsertAsync(
        Guid projectId,
        string contextId,
        Guid agentId,
        string externalAgentName,
        string providerSessionId,
        string user,
        CancellationToken cancellationToken = default)
    {
        var normalizedAgentName = NormalizeExternalAgentName(externalAgentName);
        var normalizedContextId = NormalizeContextId(contextId);
        var normalizedProviderSessionId = NormalizeProviderSessionId(providerSessionId);
        var normalizedUser = string.IsNullOrWhiteSpace(user) ? "system" : user.Trim();
        var now = _timeProvider.GetUtcNow();

        if (string.IsNullOrWhiteSpace(normalizedAgentName))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "External agent name is required.");
        }

        if (string.IsNullOrWhiteSpace(normalizedContextId))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Context id is required.");
        }

        var projectContext = await _contextRepository.Queryable
            .SingleOrDefaultAsync(
                context => context.ProjectId == projectId && context.ContextId == normalizedContextId,
                cancellationToken);

        if (projectContext == null)
        {
            throw new AgwException(ErrorCodes.ResourceNotFound, "Project context not found.");
        }

        var binding = await _bindingRepository.Queryable
            .SingleOrDefaultAsync(
                existing => existing.ProjectContextId == projectContext.Id
                    && existing.AgentId == agentId
                    && existing.ExternalAgentName == normalizedAgentName,
                cancellationToken);

        if (binding == null)
        {
            binding = new TaskSessionBinding
            {
                Id = Guid.NewGuid(),
                ProjectContextId = projectContext.Id,
                AgentId = agentId,
                ExternalAgentName = normalizedAgentName,
                ProviderSessionId = normalizedProviderSessionId,
                CreateBy = normalizedUser,
                CreateTime = now
            };
            await _bindingRepository.AddAsync(binding);
            try
            {
                await _unitOfWork.SaveChangesAsync();
                return binding;
            }
            catch (DbUpdateException)
            {
                _bindingRepository.Remove(binding);
                binding = await _bindingRepository.Queryable
                    .SingleOrDefaultAsync(
                        existing => existing.ProjectContextId == projectContext.Id
                            && existing.AgentId == agentId
                            && existing.ExternalAgentName == normalizedAgentName,
                        cancellationToken);
                if (binding == null)
                {
                    throw;
                }
            }
        }

        if (binding.ProviderSessionId != normalizedProviderSessionId)
        {
            binding.ProviderSessionId = normalizedProviderSessionId;
            binding.UpdateBy = normalizedUser;
            binding.UpdateTime = now;
            _bindingRepository.Update(binding);
        }

        await _unitOfWork.SaveChangesAsync();
        return binding;
    }

    public async Task DeleteByContextAsync(
        Guid projectContextId,
        CancellationToken cancellationToken = default)
    {
        await _bindingRepository.Queryable
            .Where(binding => binding.ProjectContextId == projectContextId)
            .ExecuteDeleteAsync(cancellationToken);

        await _unitOfWork.SaveChangesAsync();
    }

    private static string NormalizeContextId(string contextId)
    {
        if (string.IsNullOrWhiteSpace(contextId))
        {
            return string.Empty;
        }

        return contextId.Trim();
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
