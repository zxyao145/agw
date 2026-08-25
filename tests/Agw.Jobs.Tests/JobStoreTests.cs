using System.Text.Json;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Jobs.Tests;

public class JobStoreTests
{
    [Fact]
    public async Task TryStartAttemptAsync_PersistsStableAttemptIdentity()
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
        var utcNow = new DateTimeOffset(2026, 7, 13, 10, 0, 0, TimeSpan.Zero);

        await using (var dbContext = new AgwDbContext(options))
        {
            dbContext.Jobs.Add(
                new Job
                {
                    Id = jobId,
                    ProjectId = Guid.CreateVersion7(),
                    AgentType = AgentRuntimeType.Agent,
                    AgentId = Guid.CreateVersion7(),
                    Name = "job",
                    TriggerType = TriggerType.Interval,
                    TriggerValue = "00:15:00",
                    NextRunTime = utcNow,
                    Status = JobStatus.Pending,
                    IsEnabled = true,
                    CreateBy = "owner",
                    CreateTime = utcNow,
                    UpdateBy = "owner",
                    UpdateTime = utcNow,
                }
            );
            await dbContext.SaveChangesAsync(cancellationToken);

            var store = new JobRepo(dbContext, new TestTimeProvider(utcNow));
            var claim = await store.TryStartAttemptAsync(jobId, cancellationToken);

            Assert.NotNull(claim);
            Assert.NotEqual(Guid.Empty, claim.ExecutionId);
            Assert.Equal(utcNow, claim.StartedAt);
            Assert.Equal(1, claim.Attempt);
        }

        await using var verifyContext = new AgwDbContext(options);
        var job = await verifyContext.Jobs.SingleAsync(cancellationToken);
        Assert.Equal(JobStatus.Running, job.Status);
        Assert.NotNull(job.ActiveExecutionId);
        Assert.Equal(utcNow, job.ActiveAttemptStartedAt);
        var json = JsonSerializer.Serialize(job);
        Assert.DoesNotContain("activeExecutionId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("activeAttemptStartedAt", json, StringComparison.Ordinal);
    }
}
