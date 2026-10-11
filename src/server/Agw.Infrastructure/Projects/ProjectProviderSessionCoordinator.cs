using Agw.Agents.Application.Persistence;
using Agw.Auth.Contracts;
using Agw.Infrastructure.Data;
using Agw.Projects.Application.Persistence;
using Agw.Projects.Contracts.Execution;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Infrastructure.Projects;

public sealed class ProjectProviderSessionCoordinator : IProjectProviderSessionCoordinator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IApplicationLock _applicationLock;

    public ProjectProviderSessionCoordinator(IServiceScopeFactory scopeFactory, IApplicationLock applicationLock)
    {
        _scopeFactory = scopeFactory;
        _applicationLock = applicationLock;
    }

    public async Task<TResult> SaveAsync<TResult>(
        ProviderSessionSaveTarget target,
        Func<ProviderSessionOperation, Task<TResult>> operation,
        CancellationToken cancellationToken = default
    )
    {
        var owner = UserInfoUtil.RequiredUserId;
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var conversation = await LockAndLoadAsync(
            dbContext,
            owner,
            target.ProjectId,
            target.ConversationId,
            target.ExpectedGeneration,
            target.AgentId,
            target.ExternalAgentName,
            cancellationToken
        );
        var result = await operation(
            CreateOperation(dbContext, conversation, target.ExpectedGeneration, cancellationToken)
        );
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task ArchiveAsync(
        ProviderSessionArchiveTarget target,
        Func<ProviderSessionOperation, Task> operation,
        CancellationToken cancellationToken = default
    )
    {
        var owner = UserInfoUtil.RequiredUserId;
        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var dbContext = services.GetRequiredService<AgwDbContext>();
        var generation =
            await dbContext
                .OwnedProjectConversations(target.ProjectId, owner)
                .Where(conversation => conversation.Id == target.ConversationId)
                .Select(conversation => (int?)conversation.Generation)
                .SingleOrDefaultAsync(cancellationToken)
            ?? throw new AgwException(ErrorCodes.ResourceNotFound, "Project conversation not found.");
        var located =
            await dbContext
                .ProjectConversationBindings.AsNoTracking()
                .Where(binding =>
                    binding.Id == target.BindingId && binding.ProjectConversationId == target.ConversationId
                )
                .Select(binding => new
                {
                    binding.AgentId,
                    binding.ExternalAgentName,
                    binding.IsActive,
                })
                .SingleOrDefaultAsync(cancellationToken)
            ?? throw new AgwException(ErrorCodes.ResourceNotFound, "Provider session binding not found.");
        if (!located.IsActive)
        {
            return;
        }

        var gate = services.GetRequiredService<IConversationExecutionGate>();
        await using var executionLease = await gate.AcquireAsync(target.ConversationId, generation, cancellationToken);
        await using var projectLease = await _applicationLock.AcquireAsync(
            ProjectLifecycleLock.GetResourceName(target.ProjectId),
            cancellationToken
        );
        await using var definitionLease = await _applicationLock.AcquireAsync(
            AgentDefinitionLock.GetResourceName(owner),
            cancellationToken
        );
        using var mutation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            executionLease.HandleLostToken,
            projectLease.HandleLostToken,
            definitionLease.HandleLostToken
        );
        var token = mutation.Token;
        await using var transaction = await dbContext.Database.BeginTransactionAsync(token);
        var conversation = await LockAndLoadAsync(
            dbContext,
            owner,
            target.ProjectId,
            target.ConversationId,
            generation,
            located.AgentId,
            located.ExternalAgentName,
            token
        );
        var binding =
            conversation.Bindings.SingleOrDefault(binding => binding.Id == target.BindingId)
            ?? throw new AgwException(ErrorCodes.ResourceNotFound, "Provider session binding not found.");
        if (!binding.IsActive)
        {
            return;
        }

        var scopeMaintenance = services.GetRequiredService<IDurableExecutionScopeMaintenance>();
        if (
            await scopeMaintenance.RepairAndCheckActiveExecutionsAsync(
                target.ProjectId,
                target.ConversationId,
                owner,
                token
            )
        )
        {
            throw new AgwException(ErrorCodes.ConversationSessionConflict);
        }

        await operation(CreateOperation(dbContext, conversation, generation, token));
        await transaction.CommitAsync(token);
    }

    /// <summary>
    /// 取得项目与对话的数据库写入锁并校验所有权和 Generation，再加载对话及目标绑定组的全部记录。
    /// Takes the project and conversation database write locks while checking ownership and generation, then loads the conversation with every record of the target binding group.
    /// </summary>
    private static async Task<ProjectConversation> LockAndLoadAsync(
        AgwDbContext dbContext,
        string owner,
        Guid projectId,
        Guid conversationId,
        int expectedGeneration,
        Guid agentId,
        string externalAgentName,
        CancellationToken cancellationToken
    )
    {
        if (!await dbContext.LockOwnedProjectAsync(projectId, owner, cancellationToken))
        {
            throw new AgwException(ErrorCodes.ConversationSessionConflict);
        }

        var locked = await dbContext
            .ProjectConversations.Where(conversation =>
                conversation.Id == conversationId
                && conversation.ProjectId == projectId
                && conversation.CreateBy == owner
                && conversation.Generation == expectedGeneration
            )
            .ExecuteUpdateAsync(
                setters =>
                    setters.SetProperty(
                        conversation => conversation.Generation,
                        conversation => conversation.Generation
                    ),
                cancellationToken
            );
        if (locked != 1)
        {
            throw new AgwException(ErrorCodes.ConversationSessionConflict);
        }

        return await dbContext
            .ProjectConversations.Include(conversation =>
                conversation.Bindings.Where(binding =>
                    binding.AgentId == agentId && binding.ExternalAgentName == externalAgentName
                )
            )
            .SingleAsync(conversation => conversation.Id == conversationId, cancellationToken);
    }

    private static ProviderSessionOperation CreateOperation(
        AgwDbContext dbContext,
        ProjectConversation conversation,
        int expectedGeneration,
        CancellationToken cancellationToken
    ) =>
        new(
            conversation,
            cancellationToken,
            token => dbContext.SaveConversationChangesAsync(conversation.Id, expectedGeneration, token)
        );
}
