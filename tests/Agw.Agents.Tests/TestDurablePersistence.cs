using Agw.Infrastructure.Agents;
using Agw.Infrastructure.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

internal static class TestDurablePersistence
{
    public static DurableExecutionScopeMaintenance Create(AgwDbContext context) =>
        new(context, TimeProvider.System, NullLogger<DurableExecutionScopeMaintenance>.Instance);
}
