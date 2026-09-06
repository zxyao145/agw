using Agw.Agents.Contracts.Catalog;
using Agw.Auth.Contracts;
using Agw.Projects.Application.Persistence;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Application;

public class TaskSessionBindingService : ITaskSessionBindingService
{
    private readonly IProjectsDbContext _dbContext;
    private readonly IAgentCatalogFacade _agentCatalog;
    private readonly IApplicationLock _applicationLock;
    private readonly TimeProvider _timeProvider;
    private readonly IUserInfoService _userInfoService;

    public TaskSessionBindingService(
        IProjectsDbContext dbContext,
        TimeProvider timeProvider,
        IUserInfoService userInfoService,
        IAgentCatalogFacade agentCatalog,
        IApplicationLock? applicationLock = null
    )
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
        _userInfoService = userInfoService;
        _agentCatalog = agentCatalog;
        _applicationLock = applicationLock ?? InMemoryApplicationLock.Shared;
    }

    public async Task<ProjectConversationBinding?> GetAsync(
        Guid projectId,
        string contextId,
        Guid agentId,
        string externalAgentName,
        CancellationToken cancellationToken = default,
        int expectedGeneration = 0
    )
    {
        var normalizedAgentName = NormalizeExternalAgentName(externalAgentName);
        var normalizedContextId = NormalizeContextId(contextId);
        var ownerUserId = ResolveOwnerUserId();
        if (string.IsNullOrWhiteSpace(normalizedAgentName) || string.IsNullOrWhiteSpace(normalizedContextId))
        {
            return null;
        }

        var projectConversation = await _dbContext
            .ProjectConversations.AsNoTracking()
            .SingleOrDefaultAsync(
                context =>
                    context.ProjectId == projectId
                    && context.ContextId == normalizedContextId
                    && context.Generation == expectedGeneration
                    && context.CreateBy == ownerUserId
                    && _dbContext.Projects.Any(project =>
                        project.Id == context.ProjectId && project.CreateBy == ownerUserId
                    ),
                cancellationToken
            );

        if (projectConversation == null)
        {
            return null;
        }

        return await _dbContext
            .ProjectConversationBindings.AsNoTracking()
            .SingleOrDefaultAsync(
                binding =>
                    binding.ProjectConversationId == projectConversation.Id
                    && binding.AgentId == agentId
                    && binding.ExternalAgentName == normalizedAgentName,
                cancellationToken
            );
    }

    public async Task<ProjectConversationBinding> UpsertAsync(
        Guid projectId,
        string contextId,
        Guid agentId,
        string externalAgentName,
        string providerSessionId,
        string user,
        CancellationToken cancellationToken = default,
        int expectedGeneration = 0
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
        await using var projectLease = await _applicationLock.AcquireAsync(
            ProjectLifecycleLock.GetResourceName(projectId),
            cancellationToken
        );
        await using var definitionLease = await _applicationLock.AcquireAsync(
            AgentDefinitionLock.GetResourceName(ownerUserId),
            cancellationToken
        );
        using var mutation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            projectLease.HandleLostToken,
            definitionLease.HandleLostToken
        );
        cancellationToken = mutation.Token;
        if (!await _agentCatalog.IsOwnedTargetAsync(AgentRuntimeType.Agent, agentId, ownerUserId, cancellationToken))
        {
            throw new AgwException(ErrorCodes.ResourceNotFound);
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

        var projectConversation = await _dbContext.ProjectConversations.SingleOrDefaultAsync(
            context =>
                context.ProjectId == projectId
                && context.ContextId == normalizedContextId
                && context.Generation == expectedGeneration
                && context.CreateBy == ownerUserId
                && _dbContext.Projects.Any(project =>
                    project.Id == context.ProjectId && project.CreateBy == ownerUserId
                ),
            cancellationToken
        );

        if (projectConversation == null)
        {
            throw new AgwException(ErrorCodes.ResourceNotFound, "Project context not found.");
        }

        var binding = await _dbContext.ProjectConversationBindings.SingleOrDefaultAsync(
            existing =>
                existing.ProjectConversationId == projectConversation.Id
                && existing.AgentId == agentId
                && existing.ExternalAgentName == normalizedAgentName,
            cancellationToken
        );

        if (binding == null)
        {
            binding = new ProjectConversationBinding
            {
                Id = Guid.CreateVersion7(),
                ProjectConversationId = projectConversation.Id,
                AgentId = agentId,
                ExternalAgentName = normalizedAgentName,
                ProviderSessionId = normalizedProviderSessionId,
                CreateBy = projectConversation.CreateBy ?? normalizedUser,
                CreateTime = now,
            };
            await _dbContext.ProjectConversationBindings.AddAsync(binding, cancellationToken);
            try
            {
                await _dbContext.SaveConversationChangesAsync(
                    projectConversation.Id,
                    expectedGeneration,
                    cancellationToken
                );
                return binding;
            }
            catch (DbUpdateException)
            {
                _dbContext.ProjectConversationBindings.Remove(binding);
                binding = await _dbContext.ProjectConversationBindings.SingleOrDefaultAsync(
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
        }

        await _dbContext.SaveConversationChangesAsync(projectConversation.Id, expectedGeneration, cancellationToken);
        return binding;
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
