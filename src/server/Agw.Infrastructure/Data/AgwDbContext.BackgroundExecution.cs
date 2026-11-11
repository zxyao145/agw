using Agw.Shared.Data.Entities.Agents;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Data;

public partial class AgwDbContext
{
    public DbSet<BackgroundExecution> BackgroundExecutions => Set<BackgroundExecution>();
}
