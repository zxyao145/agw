using Agw.Agents.Contracts.Catalog;
using Agw.Auth.Contracts;
using Agw.Projects.Application.Persistence;
using Agw.Projects.Domain.Behaviors;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Application;

public class TaskSessionBindingService : ITaskSessionBindingService
{
    private readonly IProjectsDbContext _dbContext;
    private readonly IProjectProviderSessionCoordinator _coordinator;
    private readonly IAgentCatalogFacade _agentCatalog;
    private readonly IApplicationLock _applicationLock;
    private readonly TimeProvider _timeProvider;
    private readonly IUserInfoService _userInfoService;

    public TaskSessionBindingService(
        IProjectsDbContext dbContext,
        IProjectProviderSessionCoordinator coordinator,
        TimeProvider timeProvider,
        IUserInfoService userInfoService,
        IAgentCatalogFacade agentCatalog,
        IApplicationLock? applicationLock = null
    )
    {
        _dbContext = dbContext;
        _coordinator = coordinator;
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
                    && binding.ExternalAgentName == normalizedAgentName
                    && binding.IsActive,
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

        var projectConversationId =
            await _dbContext
                .ProjectConversations.AsNoTracking()
                .Where(context =>
                    context.ProjectId == projectId
                    && context.ContextId == normalizedContextId
                    && context.Generation == expectedGeneration
                    && context.CreateBy == ownerUserId
                    && _dbContext.Projects.Any(project =>
                        project.Id == context.ProjectId && project.CreateBy == ownerUserId
                    )
                )
                .Select(context => (Guid?)context.Id)
                .SingleOrDefaultAsync(cancellationToken)
            ?? throw new AgwException(ErrorCodes.ResourceNotFound, "Project context not found.");

        return await _coordinator.SaveAsync(
            new ProviderSessionSaveTarget(
                projectId,
                projectConversationId,
                expectedGeneration,
                agentId,
                normalizedAgentName
            ),
            async operation =>
            {
                var behavior = new ProjectConversationBehavior(operation.Conversation);
                var current = behavior.PrepareProviderSessionActivation(
                    agentId,
                    normalizedAgentName,
                    normalizedProviderSessionId,
                    normalizedUser,
                    now
                );
                if (current != null)
                {
                    return current;
                }

                // 先保存原记录的停用，新记录在第二次保存时加入，同一事务内两次保存都满足唯一生效约束。
                // The previous record's archive is saved first and the new record joins in the second save, so both saves in one transaction satisfy the single-active constraint.
                await operation.SaveChangesAsync(operation.CancellationToken);
                var binding = behavior.ActivateProviderSession(
                    agentId,
                    normalizedAgentName,
                    normalizedProviderSessionId,
                    normalizedUser,
                    now
                );
                await operation.SaveChangesAsync(operation.CancellationToken);
                return binding;
            },
            cancellationToken
        );
    }

    public async Task<IReadOnlyList<ProjectConversationBinding>> ListAsync(
        Guid projectId,
        Guid conversationId,
        CancellationToken cancellationToken = default
    )
    {
        EnsureConversationIdentity(projectId, conversationId);
        var ownerUserId = ResolveOwnerUserId();
        var exists = await _dbContext
            .ProjectConversations.AsNoTracking()
            .AnyAsync(
                conversation =>
                    conversation.Id == conversationId
                    && conversation.ProjectId == projectId
                    && conversation.CreateBy == ownerUserId
                    && _dbContext.Projects.Any(project =>
                        project.Id == conversation.ProjectId && project.CreateBy == ownerUserId
                    ),
                cancellationToken
            );
        if (!exists)
        {
            throw new AgwException(ErrorCodes.ResourceNotFound, "Project conversation not found.");
        }

        var bindings = await _dbContext
            .ProjectConversationBindings.AsNoTracking()
            .Where(binding => binding.ProjectConversationId == conversationId)
            .ToListAsync(cancellationToken);
        // 单个对话的记录数量有限，在内存中排序以兼容 SQLite 的 DateTimeOffset。
        // A single conversation has few records, so they are ordered in memory to stay compatible with SQLite's DateTimeOffset.
        return bindings
            .OrderByDescending(binding => binding.CreateTime)
            .ThenByDescending(binding => binding.Id)
            .ToList();
    }

    public Task ArchiveAsync(
        Guid projectId,
        Guid conversationId,
        Guid bindingId,
        CancellationToken cancellationToken = default
    )
    {
        EnsureConversationIdentity(projectId, conversationId);
        if (bindingId == Guid.Empty)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Binding id is required.");
        }

        var user = ResolveOwnerUserId();
        var now = _timeProvider.GetUtcNow();
        return _coordinator.ArchiveAsync(
            new ProviderSessionArchiveTarget(projectId, conversationId, bindingId),
            async operation =>
            {
                if (
                    new ProjectConversationBehavior(operation.Conversation).ArchiveProviderSession(bindingId, user, now)
                )
                {
                    await operation.SaveChangesAsync(operation.CancellationToken);
                }
            },
            cancellationToken
        );
    }

    private static void EnsureConversationIdentity(Guid projectId, Guid conversationId)
    {
        if (projectId == Guid.Empty || conversationId == Guid.Empty)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Project id and conversation id are required.");
        }
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
