using Agw.Infrastructure.Agents;
using Agw.Infrastructure.Data;
using Agw.Shared.Coordination;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

internal static class TestDurablePersistence
{
    public static DurableExecutionScopeMaintenance Create(AgwDbContext context) =>
        new(
            context,
            InMemoryApplicationLock.Shared,
            TimeProvider.System,
            NullLogger<DurableExecutionScopeMaintenance>.Instance
        );
}
