using System.IO.Compression;
using System.Security.Claims;
using Agw.Auth.Contracts;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Data.Interceptors;
using Agw.Infrastructure.Repositories;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Agw.Skills.Application;
using Agw.Skills.Application.Remote;
using Agw.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Skills.Tests;

public class SkillAppServicePersistenceTests
{
    [Theory]
    [InlineData(SkillKind.Local)]
    [InlineData(SkillKind.Remote)]
    public async Task CreateAndUpdateAsync_PersistsAuditAndPreservesContentLocation(SkillKind kind)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var userScope = UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "tester")], "Test"))
        );
        var root = Path.Combine(Path.GetTempPath(), $"agw-skill-persistence-{Guid.CreateVersion7():N}");
        var paths = AgwDataPaths.Resolve(root, "/unused");
        paths.EnsureCreated();
        try
        {
            var createdAt = new DateTimeOffset(2026, 9, 5, 1, 0, 0, TimeSpan.Zero);
            var clock = new TestTimeProvider(createdAt);
            var auditUser = new AuditUserIdProvider();
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(cancellationToken);
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(
                    new EntityCreatorInterceptor(auditUser, clock),
                    new EntityModifierInterceptor(auditUser, clock)
                )
                .Options;
            await using var context = new AgwDbContext(options);
            await context.Database.EnsureCreatedAsync(cancellationToken);
            var remote = new RemoteClient();
            var service = new SkillAppService(
                context,
                new TestAgentReferenceFacade(new EfRepository<AgentSkillRelation>(context), context),
                paths,
                NullLogger<SkillAppService>.Instance,
                remote,
                new RefreshLock(),
                clock,
                new TestCurrentUser("tester")
            );

            // Act
            var result = await service.CreateAsync(
                new Skill
                {
                    Kind = kind,
                    Name = "test-skill",
                    Description = "description",
                },
                kind == SkillKind.Local ? CreateArchive() : null,
                "tester",
                kind == SkillKind.Remote ? "https://example.test/skill" : null,
                cancellationToken
            );

            // Assert
            var id = result.Skill.Id;
            var expectedPath = kind == SkillKind.Local ? $"skills/{id:N}" : string.Empty;
            Assert.NotEqual(Guid.Empty, id);
            Assert.Equal("tester", result.Skill.CreateBy);
            Assert.Equal(createdAt, result.Skill.CreateTime);
            Assert.Equal(expectedPath, result.Skill.ContentPath);

            for (var update = 1; update <= 2; update++)
            {
                context.ChangeTracker.Clear();
                var updatedAt = createdAt.AddMinutes(update);
                clock.SetUtcNow(updatedAt);
                remote.Description = $"description-{update}";
                await service.UpdateAsync(
                    id,
                    "test-skill",
                    remote.Description,
                    null,
                    "tester",
                    cancellationToken: cancellationToken
                );
                context.ChangeTracker.Clear();
                var persisted = await context.Skills.SingleAsync(cancellationToken);

                Assert.Equal(expectedPath, persisted.ContentPath);
                Assert.Equal(remote.Description, persisted.Description);
                Assert.Equal("tester", persisted.CreateBy);
                Assert.Equal(createdAt, persisted.CreateTime);
                Assert.Equal("tester", persisted.UpdateBy);
                Assert.Equal(updatedAt, persisted.UpdateTime);
            }
            if (kind == SkillKind.Local)
            {
                Assert.True(File.Exists(Path.Combine(root, expectedPath, "SKILL.md")));
                Assert.Empty(await context.RemoteSkillCaches.ToListAsync(cancellationToken));
            }
            else
            {
                var cache = await context.RemoteSkillCaches.SingleAsync(cancellationToken);
                Assert.Equal(id, cache.SkillId);
                Assert.Equal(clock.GetUtcNow(), cache.FetchedAt);
                Assert.Equal(
                    remote.Description,
                    RemoteSkillDefinitionSerializer.Deserialize(cache.ContentJson)!.Description
                );
            }

            // A foreign owner sees the same missing result and cannot mutate persisted content.
            using var foreignScope = UserInfoUtil.Push(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "other-user")], "Test"))
            );
            Assert.Null(
                await service.UpdateAsync(
                    id,
                    "changed",
                    "changed",
                    null,
                    "other-user",
                    cancellationToken: cancellationToken
                )
            );
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateAsync_BuiltInSkill_RejectsBeforePersistence()
    {
        var service = new SkillAppService(
            null!,
            null!,
            null!,
            NullLogger<SkillAppService>.Instance,
            null!,
            null!,
            TimeProvider.System,
            new TestCurrentUser("tester")
        );

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            service.CreateAsync(
                new Skill { Kind = SkillKind.BuiltIn },
                null,
                "tester",
                cancellationToken: TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(ErrorCodes.SkillKindInvalid.Code, exception.Code);
    }

    private static IFormFile CreateArchive()
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry("test-skill/SKILL.md").Open()))
        {
            writer.Write("---\nname: test-skill\ndescription: description\n---\nInstructions");
        }
        stream.Position = 0;
        return new FormFile(stream, 0, stream.Length, "archive", "skill.zip");
    }

    private sealed class AuditUserIdProvider : IEntityAuditUserIdProvider
    {
        public string GetUserId() => UserInfoUtil.RequiredUserId;
    }

    private sealed class RemoteClient : IRemoteSkillClient
    {
        public string Description { get; set; } = "description";

        public string NormalizeUrl(string? remoteUrl) => remoteUrl!;

        public Task<RemoteSkillDefinition> FetchAsync(
            string remoteUrl,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new RemoteSkillDefinition("test-skill", Description, "Instructions", []));
    }

    private sealed class RefreshLock : IRemoteSkillRefreshLock
    {
        public Task<IAsyncDisposable> AcquireAsync(Guid skillId, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable>(new Lease());

        private sealed class Lease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
