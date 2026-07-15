using System.Data.Common;
using System.Net;
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
            builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
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
