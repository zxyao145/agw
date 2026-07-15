using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Jobs.Application.Services;
using Agw.Jobs.Contracts;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Data.Entities.Projects;
using Agw.Testing;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Jobs.Tests;

public class JobAppServiceTests
{
    private static readonly DateTimeOffset UtcNow = new(2026, 7, 15, 23, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateAsync_BlankName_GeneratesCountBasedUtcName()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await JobAppServiceFixture.CreateAsync(2, cancellationToken);

        var job = await fixture.Service.CreateAsync(CreateRequest("   "), "test-user");

        Assert.Equal("job-3-20260715", job.Name);
    }

    [Fact]
    public async Task CreateAsync_ProvidedName_TrimsAndPreservesName()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await JobAppServiceFixture.CreateAsync(0, cancellationToken);

        var job = await fixture.Service.CreateAsync(CreateRequest("  Nightly Job  "), "test-user");

        Assert.Equal("Nightly Job", job.Name);
    }

    [Fact]
    public async Task UpdateAsync_BlankName_GeneratesCountBasedUtcName()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await JobAppServiceFixture.CreateAsync(2, cancellationToken);

        var job = await fixture.Service.UpdateAsync(
            fixture.FirstJobId,
            new JobUpdateRequest
            {
                ProjectId = Guid.NewGuid(),
                Name = "",
                TriggerType = TriggerType.Interval,
                TriggerValue = "00:01:00",
                MaxRetryCount = 3,
                IsEnabled = true,
                Status = JobStatus.Pending
            },
            "test-user");

        Assert.NotNull(job);
        Assert.Equal("job-3-20260715", job!.Name);
    }

    private static JobCreateRequest CreateRequest(string name)
    {
        return new JobCreateRequest
        {
            ProjectId = Guid.NewGuid(),
            Name = name,
            TriggerType = TriggerType.Interval,
            TriggerValue = "00:01:00",
            MaxRetryCount = 3,
            IsEnabled = true
        };
    }

    private sealed class JobAppServiceFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly AgwDbContext _dbContext;

        private JobAppServiceFixture(
            SqliteConnection connection,
            AgwDbContext dbContext,
            JobAppService service,
            Guid firstJobId)
        {
            _connection = connection;
            _dbContext = dbContext;
            Service = service;
            FirstJobId = firstJobId;
        }

        public JobAppService Service { get; }
        public Guid FirstJobId { get; }

        public static async Task<JobAppServiceFixture> CreateAsync(
            int jobCount,
            CancellationToken cancellationToken)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(cancellationToken);

            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .UseSnakeCaseNamingConvention()
                .Options;
            var dbContext = new AgwDbContext(options);
            await dbContext.Database.EnsureCreatedAsync(cancellationToken);

            var jobs = Enumerable.Range(1, jobCount)
                .Select(index => new Job
                {
                    Id = Guid.NewGuid(),
                    ProjectId = Guid.NewGuid(),
                    Name = $"Existing Job {index}",
                    TriggerType = TriggerType.Interval,
                    TriggerValue = "00:01:00",
                    NextRunTime = UtcNow.AddMinutes(index),
                    MaxRetryCount = 3,
                    IsEnabled = true,
                    Status = JobStatus.Pending,
                    CreateBy = "test-user",
                    CreateTime = UtcNow,
                    UpdateBy = "test-user",
                    UpdateTime = UtcNow
                })
                .ToList();

            if (jobs.Count > 0)
            {
                await dbContext.Jobs.AddRangeAsync(jobs, cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            var timeProvider = new TestTimeProvider(UtcNow);
            var service = new JobAppService(
                new JobRepo(dbContext, timeProvider),
                new EfRepository<JobLog>(dbContext),
                new EfRepository<TaskRecord>(dbContext),
                new EfRepository<ProjectContext>(dbContext),
                new UnitOfWork(dbContext),
                new JobTimeCalculator(),
                new JobDomainEventDispatcher(),
                timeProvider);

            return new JobAppServiceFixture(
                connection,
                dbContext,
                service,
                jobs.FirstOrDefault()?.Id ?? Guid.Empty);
        }

        public async ValueTask DisposeAsync()
        {
            await _dbContext.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
