using Agw.Infrastructure.Data;
using Agw.Jobs.Application.Persistence;
using Agw.Jobs.Scheduling.Attempts;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;

namespace Agw.Infrastructure.Jobs;

public sealed class JobOutcomeTransaction : IJobOutcomeTransaction
{
    private readonly AgwDbContext _dbContext;
    private readonly IApplicationLock _applicationLock;

    public JobOutcomeTransaction(AgwDbContext dbContext, IApplicationLock applicationLock)
    {
        _dbContext = dbContext;
        _applicationLock = applicationLock;
    }

    public async Task<JobAttemptResult> ExecuteAsync(
        Guid jobId,
        Func<CancellationToken, Task<JobAttemptResult>> record,
        CancellationToken cancellationToken
    )
    {
        var lease = await _applicationLock.AcquireAsync(JobOutcomeLock.GetResourceName(jobId), cancellationToken);
        await using var linked = new ApplicationLockScope(lease, cancellationToken);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(linked.Token);
        try
        {
            var result = await record(linked.Token).ConfigureAwait(false);
            await transaction.CommitAsync(linked.Token).ConfigureAwait(false);
            return result;
        }
        catch
        {
            // SaveChanges inside Projects may have accepted changes before the outer transaction failed.
            // A retry in this scope must reload database state instead of reusing those accepted values.
            _dbContext.ChangeTracker.Clear();
            throw;
        }
    }
}
