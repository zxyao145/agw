using Hangfire.Server;
using Microsoft.Extensions.Logging;

namespace Agw.Jobs.Services;

public sealed class ManagedHangfireJobExecutor
{
    private readonly ILogger<ManagedHangfireJobExecutor> _logger;

    public ManagedHangfireJobExecutor(ILogger<ManagedHangfireJobExecutor> logger)
    {
        _logger = logger;
    }

    public Task ExecuteAsync(Guid definitionId, PerformContext? context, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Executing managed Hangfire job definition {DefinitionId}. BackgroundJobId={BackgroundJobId}",
            definitionId,
            context?.BackgroundJob?.Id);
        return Task.CompletedTask;
    }
}
