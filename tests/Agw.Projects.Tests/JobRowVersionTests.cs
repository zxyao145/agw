using Agw.Infrastructure.Data;
using Agw.Shared.Data.Entities.Jobs;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Tests;

public class JobRowVersionTests
{
    [Fact]
    public async Task SaveChangesAsync_JobAddedAndUpdated_ManagesRowVersionForSqlite()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;

        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
        }

        var jobId = Guid.CreateVersion7();
        byte[] createdRowVersion;

        await using (var createContext = new AgwDbContext(options))
        {
            var job = new Job
            {
                Id = jobId,
                ProjectId = Guid.CreateVersion7(),
                Name = "Nightly sync",
                TriggerType = TriggerType.Once,
                TriggerValue = "2026-03-28T13:00:00Z",
                NextRunTime = TimeProvider.System.GetUtcNow(),
                Status = JobStatus.Pending,
                IsEnabled = true,
                CreateBy = "tester",
                CreateTime = TimeProvider.System.GetUtcNow(),
                UpdateBy = "tester",
                UpdateTime = TimeProvider.System.GetUtcNow()
            };

            createContext.Jobs.Add(job);
            await createContext.SaveChangesAsync(cancellationToken);

            createdRowVersion = job.RowVersion.ToArray();
        }

        Assert.Equal(16, createdRowVersion.Length);

        await using (var updateContext = new AgwDbContext(options))
        {
            var job = await updateContext.Jobs.SingleAsync(x => x.Id == jobId, cancellationToken);
            var originalRowVersion = job.RowVersion.ToArray();

            job.Name = "Nightly sync updated";
            job.UpdateBy = "tester-2";
            job.UpdateTime = TimeProvider.System.GetUtcNow();

            await updateContext.SaveChangesAsync(cancellationToken);

            Assert.Equal(16, job.RowVersion.Length);
            Assert.False(job.RowVersion.SequenceEqual(originalRowVersion));
        }
    }
}
