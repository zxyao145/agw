using Agw.Jobs.Contracts;
using Agw.Tasks.Services;

namespace Agw.Jobs.Services;

public interface IHangfireJobAppService
{
    Task<IReadOnlyList<HangfireJobSummaryResponse>> ListAsync(CancellationToken cancellationToken = default);

    Task<HangfireJobDetailResponse?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ApplicationResult<HangfireJobDetailResponse>> CreateAsync(
        HangfireJobUpsertRequest request,
        string user,
        CancellationToken cancellationToken = default);

    Task<ApplicationResult<HangfireJobDetailResponse>> UpdateAsync(
        Guid id,
        HangfireJobUpsertRequest request,
        string user,
        CancellationToken cancellationToken = default);

    Task<ApplicationResult<HangfireJobDetailResponse>> PauseAsync(
        Guid id,
        string user,
        CancellationToken cancellationToken = default);

    Task<ApplicationResult<HangfireJobDetailResponse>> StartAsync(
        Guid id,
        string user,
        CancellationToken cancellationToken = default);

    Task<ApplicationResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
