using Agw.Auth.Contracts;
using Agw.Infrastructure.Data;
using Agw.Projects.Contracts.Execution;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Projects;

public sealed class ConversationExecutionGate : IConversationExecutionGate
{
    private readonly AgwDbContext _dbContext;
    private readonly IApplicationLock _applicationLock;
    private readonly TimeProvider _timeProvider;

    public ConversationExecutionGate(
        AgwDbContext dbContext,
        IApplicationLock applicationLock,
        TimeProvider timeProvider
    )
    {
        _dbContext = dbContext;
        _applicationLock = applicationLock;
        _timeProvider = timeProvider;
    }

    public async Task<IApplicationLockLease> AcquireAsync(
        Guid conversationId,
        int expectedGeneration,
        CancellationToken cancellationToken = default
    )
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100), _timeProvider);
        using var acquire = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        IApplicationLockLease lease;
        try
        {
            lease = await _applicationLock.AcquireAsync(
                ConversationExecutionLock.GetResourceName(conversationId),
                acquire.Token
            );
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AgwException(ErrorCodes.ConversationSessionConflict);
        }

        try
        {
            var owner = UserInfoUtil.RequiredUserId;
            using var validation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                lease.HandleLostToken
            );
            if (
                !await _dbContext
                    .ProjectConversations.AsNoTracking()
                    .AnyAsync(
                        conversation =>
                            conversation.Id == conversationId
                            && conversation.CreateBy == owner
                            && conversation.Project!.CreateBy == owner
                            && conversation.Generation == expectedGeneration,
                        validation.Token
                    )
            )
            {
                throw new AgwException(ErrorCodes.ConversationSessionConflict);
            }
            return lease;
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }
}
