using Agw.Auth.Contracts;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Application;

public class TaskSessionBindingService : ITaskSessionBindingService
{
    private readonly IRepository<TaskSessionBinding> _bindingRepository;
    private readonly IRepository<ProjectConversation> _contextRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly IUserInfoService _userInfoService;

    public TaskSessionBindingService(
        IRepository<TaskSessionBinding> bindingRepository,
        IRepository<ProjectConversation> contextRepository,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        IUserInfoService userInfoService
    )
    {
        _bindingRepository = bindingRepository;
        _contextRepository = contextRepository;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
        _userInfoService = userInfoService;
    }

    public async Task<TaskSessionBinding?> GetAsync(
        Guid projectId,
        string contextId,
        Guid agentId,
        string externalAgentName,
        CancellationToken cancellationToken = default
    )
    {
        var normalizedAgentName = NormalizeExternalAgentName(externalAgentName);
        var normalizedContextId = NormalizeContextId(contextId);
        var ownerUserId = ResolveOwnerUserId();
        if (string.IsNullOrWhiteSpace(normalizedAgentName) || string.IsNullOrWhiteSpace(normalizedContextId))
        {
            return null;
        }

        var projectConversation = await _contextRepository
            .Queryable.AsNoTracking()
            .SingleOrDefaultAsync(
                context =>
                    context.ProjectId == projectId
                    && context.ContextId == normalizedContextId
                    && context.CreateBy == ownerUserId,
                cancellationToken
            );

        if (projectConversation == null)
        {
            return null;
        }

        return await _bindingRepository
            .Queryable.AsNoTracking()
            .SingleOrDefaultAsync(
                binding =>
                    binding.ProjectConversationId == projectConversation.Id
                    && binding.AgentId == agentId
                    && binding.ExternalAgentName == normalizedAgentName,
                cancellationToken
            );
    }

    public async Task<TaskSessionBinding> UpsertAsync(
        Guid projectId,
        string contextId,
        Guid agentId,
        string externalAgentName,
        string providerSessionId,
        string user,
        CancellationToken cancellationToken = default
    )
    {
        var normalizedAgentName = NormalizeExternalAgentName(externalAgentName);
        var normalizedContextId = NormalizeContextId(contextId);
        var normalizedProviderSessionId = NormalizeProviderSessionId(providerSessionId);
        var ownerUserId = ResolveOwnerUserId();
        var normalizedUser = string.IsNullOrWhiteSpace(user) ? ownerUserId : user.Trim();
        if (!string.Equals(ownerUserId, normalizedUser, StringComparison.Ordinal))
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }
        var now = _timeProvider.GetUtcNow();

        if (string.IsNullOrWhiteSpace(normalizedAgentName))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "External agent name is required.");
        }

        if (string.IsNullOrWhiteSpace(normalizedContextId))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Context id is required.");
        }

        var projectConversation = await _contextRepository.Queryable.SingleOrDefaultAsync(
            context =>
                context.ProjectId == projectId
                && context.ContextId == normalizedContextId
                && context.CreateBy == ownerUserId,
            cancellationToken
        );

        if (projectConversation == null)
        {
            throw new AgwException(ErrorCodes.ResourceNotFound, "Project context not found.");
        }

        var binding = await _bindingRepository.Queryable.SingleOrDefaultAsync(
            existing =>
                existing.ProjectConversationId == projectConversation.Id
                && existing.AgentId == agentId
                && existing.ExternalAgentName == normalizedAgentName,
            cancellationToken
        );

        if (binding == null)
        {
            binding = new TaskSessionBinding
            {
                Id = Guid.CreateVersion7(),
                ProjectConversationId = projectConversation.Id,
                AgentId = agentId,
                ExternalAgentName = normalizedAgentName,
                ProviderSessionId = normalizedProviderSessionId,
                CreateBy = projectConversation.CreateBy ?? normalizedUser,
                CreateTime = now,
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
                binding = await _bindingRepository.Queryable.SingleOrDefaultAsync(
                    existing =>
                        existing.ProjectConversationId == projectConversation.Id
                        && existing.AgentId == agentId
                        && existing.ExternalAgentName == normalizedAgentName,
                    cancellationToken
                );
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

    public async Task DeleteByConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var ownerUserId = ResolveOwnerUserId();
        if (
            !await _contextRepository.Queryable.AnyAsync(
                conversation => conversation.Id == conversationId && conversation.CreateBy == ownerUserId,
                cancellationToken
            )
        )
        {
            return;
        }

        await _bindingRepository
            .Queryable.Where(binding => binding.ProjectConversationId == conversationId)
            .ExecuteDeleteAsync(cancellationToken);

        await _unitOfWork.SaveChangesAsync();
    }

    private string ResolveOwnerUserId() => _userInfoService.RequiredUserId;

    /// <summary>
    /// 将可用的 context ID 转换为规范格式，并将空白输入保留为空字符串。
    /// </summary>
    private static string NormalizeContextId(string contextId)
    {
        if (string.IsNullOrWhiteSpace(contextId))
        {
            return string.Empty;
        }

        return ContextIdUtil.NormalizeContextId(contextId);
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
