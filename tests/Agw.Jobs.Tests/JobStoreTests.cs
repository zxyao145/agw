using System.Reflection;
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
    public async Task AddExecutionLogAsync_PersistsTaskIdIntoJobLog()
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
        var taskId = Guid.CreateVersion7();
        var utcNow = new DateTimeOffset(2026, 7, 13, 10, 0, 0, TimeSpan.Zero);
        var startTime = utcNow;
        var endTime = startTime.AddMinutes(1);

        await using (var dbContext = new AgwDbContext(options))
        {
            var store = new JobRepo(dbContext, new TestTimeProvider(utcNow));
            var method = typeof(JobRepo).GetMethod(nameof(JobRepo.AddExecutionLogAsync));

            Assert.NotNull(method);

            var parameters = method!.GetParameters().Select(parameter => parameter.Name ?? string.Empty).ToArray();
            Assert.Equal(
                ["jobId", "taskId", "startTime", "endTime", "success", "attempt", "errorMessage", "cancellationToken"],
                parameters
            );

            var invokeResult = method.Invoke(
                store,
                [jobId, taskId, startTime, endTime, true, 1, null, cancellationToken]
            );

            var task = Assert.IsAssignableFrom<Task>(invokeResult);
            await task;
        }

        await using var verifyContext = new AgwDbContext(options);
        var log = await verifyContext.JobLogs.SingleAsync(cancellationToken);
        var taskIdProperty = typeof(JobLog).GetProperty("TaskId", BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(taskIdProperty);
        Assert.Equal(jobId, log.JobId);
        Assert.Equal(taskId, Assert.IsType<Guid>(taskIdProperty!.GetValue(log)));
        Assert.Equal(utcNow, log.CreateTime);
        Assert.Equal(utcNow, log.UpdateTime);
    }
}
