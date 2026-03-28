using Agw.Domain.Entities;
using Agw.Infrastructure.Data;
using Agw.Jobs.Enums;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Tasks.Tests;

public class JobRowVersionTests
{
    [Fact]
    public async Task SaveChangesAsync_JobAddedAndUpdated_ManagesRowVersionForSqlite()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = new DbContextOptionsBuilder<LlmDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;

        await using (var setupContext = new LlmDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
        }

        var jobId = Guid.NewGuid();
        byte[] createdRowVersion;

        await using (var createContext = new LlmDbContext(options))
        {
            var job = new Job
            {
                Id = jobId,
                ProjectId = Guid.NewGuid(),
                Name = "Nightly sync",
                TriggerType = TriggerType.Once,
                TriggerValue = "2026-03-28T13:00:00Z",
                NextRunTime = DateTimeOffset.UtcNow,
                Status = JobStatus.Pending,
                IsEnabled = true,
                CreateBy = "tester",
                CreateTime = DateTime.UtcNow,
                UpdateBy = "tester",
                UpdateTime = DateTime.UtcNow
            };

            createContext.Jobs.Add(job);
            await createContext.SaveChangesAsync(cancellationToken);

            createdRowVersion = job.RowVersion.ToArray();
        }

        Assert.Equal(16, createdRowVersion.Length);

        await using (var updateContext = new LlmDbContext(options))
        {
            var job = await updateContext.Jobs.SingleAsync(x => x.Id == jobId, cancellationToken);
            var originalRowVersion = job.RowVersion.ToArray();

            job.Name = "Nightly sync updated";
            job.UpdateBy = "tester-2";
            job.UpdateTime = DateTime.UtcNow;

            await updateContext.SaveChangesAsync(cancellationToken);

            Assert.Equal(16, job.RowVersion.Length);
            Assert.False(job.RowVersion.SequenceEqual(originalRowVersion));
        }
    }
}
