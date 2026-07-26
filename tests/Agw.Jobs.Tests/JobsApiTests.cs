using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Jobs.Api;
using Agw.Jobs.Application.Services;
using Agw.Jobs.Scheduling;
using Agw.Jobs.Scheduling.Coordination;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Data.Repositories;
using Agw.Testing;

using Bens.Results;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Jobs.Tests;

public class JobsApiTests
{
    [Fact]
    public async Task ListJobs_ReturnsBensResultsEnvelope()
    {
        await using var fixture = await JobsApiFixture.CreateAsync(
            TestContext.Current.CancellationToken);

        var response = await fixture.Client.GetAsync(
            "/api/jobs",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));
        Assert.Equal(0, body.RootElement.GetProperty("code").GetInt32());
        Assert.Equal("OK", body.RootElement.GetProperty("title").GetString());
        Assert.Empty(body.RootElement.GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task UpdateEnabled_UpdatesOnlyEnabledStateAndReturnsBensResultsEnvelope()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await JobsApiFixture.CreateAsync(cancellationToken);
        var jobId = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();
        var nextRunTime = new DateTimeOffset(2026, 7, 15, 9, 0, 0, TimeSpan.Zero);
        await fixture.SeedJobAsync(
            new Job
            {
                Id = jobId,
                ProjectId = projectId,
                Name = "Scheduled Job",
                TriggerType = TriggerType.Cron,
                TriggerValue = "*/5 * * * *",
                NextRunTime = nextRunTime,
                MaxRetryCount = 4,
                IsEnabled = true,
                Status = JobStatus.Pending,
                CreateBy = "seed",
                CreateTime = nextRunTime.AddHours(-1),
                UpdateBy = "seed",
                UpdateTime = nextRunTime.AddHours(-1)
            },
            cancellationToken);

        var response = await fixture.Client.PutAsJsonAsync(
            "/api/jobs/enabled",
            new { jobId, isEnabled = false },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(0, body.RootElement.GetProperty("code").GetInt32());
        Assert.False(body.RootElement.GetProperty("data").GetProperty("isEnabled").GetBoolean());

        var persisted = await fixture.GetJobAsync(jobId, cancellationToken);
        Assert.False(persisted.IsEnabled);
        Assert.Equal(projectId, persisted.ProjectId);
        Assert.Equal("Scheduled Job", persisted.Name);
        Assert.Equal("*/5 * * * *", persisted.TriggerValue);
        Assert.Equal(nextRunTime, persisted.NextRunTime);
        Assert.Equal(4, persisted.MaxRetryCount);
        Assert.Equal(JobStatus.Pending, persisted.Status);
    }

    private sealed class JobsApiFixture : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly DbConnection _connection;

        private JobsApiFixture(WebApplication app, DbConnection connection, HttpClient client)
        {
            _app = app;
            _connection = connection;
            Client = client;
        }

        public HttpClient Client { get; }

        public async Task SeedJobAsync(Job job, CancellationToken cancellationToken)
        {
            await using var scope = _app.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
            await dbContext.Jobs.AddAsync(job, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        public async Task<Job> GetJobAsync(Guid jobId, CancellationToken cancellationToken)
        {
            await using var scope = _app.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
            return await dbContext.Jobs
                .AsNoTracking()
                .SingleAsync(job => job.Id == jobId, cancellationToken);
        }

        public static async Task<JobsApiFixture> CreateAsync(CancellationToken cancellationToken)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(cancellationToken);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddApiResult();
            builder.Services.AddSingleton<TimeProvider>(
                new TestTimeProvider(new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.Zero)));
            builder.Services.AddDbContext<AgwDbContext>(options =>
                options.UseSqlite(connection).UseSnakeCaseNamingConvention());
            builder.Services.AddScoped<DbContext>(serviceProvider =>
                serviceProvider.GetRequiredService<AgwDbContext>());
            builder.Services.AddScoped(typeof(IRepository<>), typeof(EfRepository<>));
            builder.Services.AddScoped<JobRepo>();
            builder.Services.AddScoped<IRepository<Job>>(serviceProvider =>
                serviceProvider.GetRequiredService<JobRepo>());
            builder.Services.AddScoped<IUnitOfWork>(serviceProvider =>
                serviceProvider.GetRequiredService<AgwDbContext>());
            builder.Services.AddSingleton<JobScheduleCalculator>();
            builder.Services.AddSingleton<JobSchedulerWakeSignal>();
            builder.Services.AddScoped<JobAppService>();

            var app = builder.Build();
            app.MapJobsApi();

            await using (var scope = app.Services.CreateAsyncScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
                await dbContext.Database.EnsureCreatedAsync(cancellationToken);
            }

            await app.StartAsync(cancellationToken);
            return new JobsApiFixture(app, connection, app.GetTestClient());
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
