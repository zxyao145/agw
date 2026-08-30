using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Agw.Jobs.Application.Persistence;

public interface IJobsDbContext : IModuleDbContext
{
    DbSet<Job> Jobs { get; }

    DbSet<JobLog> JobLogs { get; }
}
