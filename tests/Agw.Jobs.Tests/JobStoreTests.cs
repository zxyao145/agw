using System.Reflection;

using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Jobs.Domain.Entities;

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

        var jobId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var startTime = DateTimeOffset.UtcNow;
        var endTime = startTime.AddMinutes(1);

        await using (var dbContext = new AgwDbContext(options))
        {
            var store = new JobRepo(dbContext);
            var method = typeof(JobRepo).GetMethod(nameof(JobRepo.AddExecutionLogAsync));

            Assert.NotNull(method);

            var parameters = method!.GetParameters().Select(parameter => parameter.Name ?? string.Empty).ToArray();
            Assert.Equal(
                ["jobId", "taskId", "startTime", "endTime", "success", "attempt", "errorMessage", "cancellationToken"],
                parameters);

            var invokeResult = method.Invoke(
                store,
                [jobId, taskId, startTime, endTime, true, 1, null, cancellationToken]);

            var task = Assert.IsAssignableFrom<Task>(invokeResult);
            await task;
        }

        await using var verifyContext = new AgwDbContext(options);
        var log = await verifyContext.JobLogs.SingleAsync(cancellationToken);
        var taskIdProperty = typeof(JobLog).GetProperty("TaskId", BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(taskIdProperty);
        Assert.Equal(jobId, log.JobId);
        Assert.Equal(taskId, Assert.IsType<Guid>(taskIdProperty!.GetValue(log)));
    }
}
