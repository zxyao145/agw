using Agw.Infrastructure.Agents;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Projects;
using Agw.Shared.Coordination;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Projects.Tests;

internal static class TestProjectPersistence
{
    public static ProjectDeletionCoordinator CreateDeletionCoordinator(AgwDbContext context) =>
        new(
            context,
            InMemoryApplicationLock.Shared,
            new DurableExecutionScopeMaintenance(
                context,
                InMemoryApplicationLock.Shared,
                TimeProvider.System,
                NullLogger<DurableExecutionScopeMaintenance>.Instance
            )
        );
}
