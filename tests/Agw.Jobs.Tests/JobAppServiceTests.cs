using System.Security.Claims;
using Agw.Auth.Contracts;
using Agw.Infrastructure.Data;
using Agw.Jobs.Application.Services;
using Agw.Jobs.Contracts;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Tests;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Jobs.Tests;

public class JobAppServiceTests
{
    [Fact]
    public async Task CreateAsync_BlankName_GeneratesCountBasedUtcName()
    {
        await using var fixture = await JobAppServiceFixture.CreateAsync(seedExistingJob: true);
        var expectedName = $"job-2-{fixture.TimeProvider.GetUtcNow():yyyyMMdd}";

        var created = await fixture.CreateJobAsync("   ");

        Assert.Equal(expectedName, created.Name);
    }

    [Fact]
    public async Task CreateAsync_ProvidedName_TrimsAndPreservesName()
    {
        await using var fixture = await JobAppServiceFixture.CreateAsync();

        var created = await fixture.CreateJobAsync("  Nightly Job  ");

        Assert.Equal("Nightly Job", created.Name);
    }

    [Fact]
    public async Task UpdateAsync_BlankName_GeneratesCountBasedUtcName()
    {
        await using var fixture = await JobAppServiceFixture.CreateAsync(seedExistingJob: true);
        var expectedName = $"job-2-{fixture.TimeProvider.GetUtcNow():yyyyMMdd}";

        var updated = await fixture.UpdateJobAsync(fixture.ExistingJob!.Id, " ");

        Assert.Equal(expectedName, updated.Name);
    }

    private sealed class JobAppServiceFixture : IAsyncDisposable
    {
        private readonly ModuleCompositionTestHost _host;
        private readonly string _workspaceRoot;

        private JobAppServiceFixture(ModuleCompositionTestHost host, string workspaceRoot)
        {
            _host = host;
            _workspaceRoot = workspaceRoot;
            TimeProvider = host.Services.GetRequiredService<TimeProvider>();
        }

        public TimeProvider TimeProvider { get; }
        public Guid ProjectId { get; private set; }
        public Guid AgentId { get; private set; }
        public JobResponse? ExistingJob { get; private set; }

        public static async Task<JobAppServiceFixture> CreateAsync(bool seedExistingJob = false)
        {
            var host = await ModuleCompositionTestHost.CreateAsync();
            var workspaceRoot = Path.Combine(
                AppContext.BaseDirectory,
                ".scratch",
                "jobs-app-service-tests",
                Guid.CreateVersion7().ToString("N")
            );
            var fixture = new JobAppServiceFixture(host, workspaceRoot);
            await fixture.SeedTargetsAsync();
            if (seedExistingJob)
            {
                fixture.ExistingJob = await fixture.CreateJobAsync("Existing Job");
            }
            return fixture;
        }

        public async Task<JobResponse> CreateJobAsync(string? name)
        {
            using var userScope = UserInfoUtil.Push(CreatePrincipal("tester"));
            await using var scope = _host.Services.CreateAsyncScope();
            return await scope
                .ServiceProvider.GetRequiredService<JobAppService>()
                .CreateAsync(
                    new JobCreateRequest
                    {
                        Name = name,
                        ProjectId = ProjectId,
                        TargetKind = AgentRuntimeType.Agent,
                        AgentId = AgentId,
                        Instructions = "Run the scheduled task.",
                        Schedule = new JobScheduleDto { Kind = "Interval", Interval = "PT15M" },
                    },
                    TestContext.Current.CancellationToken
                );
        }

        public async Task<JobResponse> UpdateJobAsync(Guid id, string? name)
        {
            using var userScope = UserInfoUtil.Push(CreatePrincipal("tester"));
            await using var scope = _host.Services.CreateAsyncScope();
            return await scope
                .ServiceProvider.GetRequiredService<JobAppService>()
                .UpdateAsync(
                    new JobUpdateRequest
                    {
                        Id = id,
                        Name = name,
                        ProjectId = ProjectId,
                        TargetKind = AgentRuntimeType.Agent,
                        AgentId = AgentId,
                        Instructions = "Run the scheduled task.",
                        Schedule = new JobScheduleDto { Kind = "Interval", Interval = "PT15M" },
                    },
                    TestContext.Current.CancellationToken
                );
        }

        private async Task SeedTargetsAsync()
        {
            ProjectId = Guid.CreateVersion7();
            AgentId = Guid.CreateVersion7();
            Directory.CreateDirectory(_workspaceRoot);
            using var userScope = UserInfoUtil.Push(CreatePrincipal("tester"));
            await using var scope = _host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
            db.Projects.Add(
                new Project
                {
                    Id = ProjectId,
                    Name = "Job service test project",
                    Workspace = _workspaceRoot,
                }
            );
            db.Agents.Add(
                new Agent
                {
                    Id = AgentId,
                    Name = "job-service-test-agent",
                    DisplayName = "Job service test agent",
                }
            );
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await _host.DisposeAsync();
            if (Directory.Exists(_workspaceRoot))
            {
                Directory.Delete(_workspaceRoot, recursive: true);
            }
        }
    }

    private static ClaimsPrincipal CreatePrincipal(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], authenticationType: "Test"));
}
