using System.IO.Compression;
using System.Security.Claims;
using Agw.Agents.Definitions.Facades;
using Agw.Auth.Application;
using Agw.Auth.Contracts;
using Agw.Infrastructure.Configuration;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Data.Interceptors;
using Agw.Infrastructure.Repositories;
using Agw.Infrastructure.Skills;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Agw.Skills.Application;
using Agw.Skills.Application.Facades;
using Agw.Skills.Application.Remote;
using Agw.Skills.Contracts;
using Agw.Skills.Domain.Services;
using Agw.Skills.Infrastructure;
using Agw.Skills.Infrastructure.Remote;
using Agw.Testing;
using Medallion.Threading.Postgres;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agw.Skills.Tests;

public class SkillAppServicePersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolveDirectoryAsync_LegacyLayout_PreservesResourcesAndLoadsSkill(bool versioned)
    {
        var token = TestContext.Current.CancellationToken;
        using var userScope = UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "tester")], "Test"))
        );
        var root = Path.Combine(AppContext.BaseDirectory, ".skill-test-data", Guid.CreateVersion7().ToString("N"));
        var paths = AgwDataPaths.Resolve(root, root);
        paths.EnsureCreated();
        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using var context = new AgwDbContext(
                new DbContextOptionsBuilder<AgwDbContext>().UseSqlite(connection).Options
            );
            await context.Database.EnsureCreatedAsync(token);
            var skillId = Guid.CreateVersion7();
            var oldPath = versioned ? $"skills/{skillId:N}-{Guid.CreateVersion7():N}" : $"skills/{skillId:N}";
            var directory = Path.Combine(root, oldPath);
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(
                Path.Combine(directory, "SKILL.md"),
                "---\nname: legacy-skill\ndescription: legacy\n---\nInstructions",
                token
            );
            await File.WriteAllTextAsync(Path.Combine(directory, "reference.md"), "Reference content", token);
            context.Skills.Add(
                new Skill
                {
                    Id = skillId,
                    Name = "legacy-skill",
                    Description = "legacy",
                    Kind = SkillKind.Local,
                    ContentPath = oldPath,
                    CreateBy = "tester",
                }
            );
            await context.SaveChangesAsync(token);
            using var services = CreateProductionServiceProvider();
            var service = CreateProductionSkillService(context, paths, new UserInfoService(), services);
            ILocalSkillContentResolver resolver = new LocalSkillContentFacade(service);
            var resolved = Assert.IsType<string>(await resolver.ResolveDirectoryAsync(skillId, token));

            Assert.Equal(
                "Reference content",
                await File.ReadAllTextAsync(Path.Combine(resolved, "reference.md"), token)
            );
            Assert.False(Directory.Exists(directory));
            Assert.Equal(resolved, Assert.IsType<string>(await resolver.ResolveDirectoryAsync(skillId, token)));
            using var source = new AgentFileSkillsSource(resolved);
            using var client = new OpenAI.Chat.ChatClient("test-model", "test-key").AsIChatClient();
            var skills = await source.GetSkillsAsync(
                new AgentSkillsSourceContext(new ChatClientAgent(client), null),
                token
            );
            Assert.Equal("legacy-skill", Assert.Single(skills).Frontmatter.Name);
            context.ChangeTracker.Clear();
            Assert.Equal(resolved, Path.Combine(root, (await context.Skills.SingleAsync(token)).ContentPath));
            context.ChangeTracker.Clear();
            Assert.True(await service.DeleteAsync(skillId, token));
            Assert.Empty(Directory.EnumerateDirectories(paths.SkillsDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveDirectoryAsync_LongLivedContextAfterLegacyMigrationAndArchiveUpdate_UsesCurrentPath()
    {
        var token = TestContext.Current.CancellationToken;
        using var userScope = UserInfoUtil.Push(
            new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "skill-runtime-scope-test")], "Test")
            )
        );
        var user = new UserInfoService();
        var root = Path.Combine(AppContext.BaseDirectory, ".skill-test-data", Guid.CreateVersion7().ToString("N"));
        var paths = AgwDataPaths.Resolve(root, root);
        paths.EnsureCreated();
        var skillId = Guid.CreateVersion7();
        var oldContentPath = $"skills/{skillId:N}";
        var oldDirectory = Path.Combine(paths.Root, oldContentPath);
        Directory.CreateDirectory(oldDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(oldDirectory, "SKILL.md"),
            "---\nname: long-lived-skill\ndescription: description\n---\nPrevious instructions",
            token
        );
        var databasePath = Path.Combine(root, "skills.db");
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite($"Data Source={databasePath};Cache=Shared")
            .Options;

        try
        {
            await using (var seedContext = new AgwDbContext(options))
            {
                await seedContext.Database.EnsureCreatedAsync(token);
                seedContext.Skills.Add(
                    new Skill
                    {
                        Id = skillId,
                        Name = "long-lived-skill",
                        Description = "description",
                        Kind = SkillKind.Local,
                        ContentPath = oldContentPath,
                        CreateBy = user.RequiredUserId,
                    }
                );
                await seedContext.SaveChangesAsync(token);
            }

            using var services = CreateProductionServiceProvider();
            await using var resolverContext = new AgwDbContext(options);
            await using var updateContext = new AgwDbContext(options);
            var resolverService = CreateProductionSkillService(resolverContext, paths, user, services);
            var updateService = CreateProductionSkillService(updateContext, paths, user, services);
            ILocalSkillContentResolver resolver = new LocalSkillContentFacade(resolverService);
            var migratedPath = Assert.IsType<string>(await resolver.ResolveDirectoryAsync(skillId, token));
            Assert.NotEqual(oldDirectory, migratedPath);
            Assert.False(Directory.Exists(oldDirectory));

            var updated = Assert.IsType<SkillDetails>(
                await updateService.UpdateAsync(
                    skillId,
                    "long-lived-skill",
                    "updated description",
                    CreateArchive("Updated skill instructions"),
                    user.RequiredUserId,
                    cancellationToken: token
                )
            );
            Assert.False(Directory.Exists(migratedPath));

            var resolvedPath = Assert.IsType<string>(await resolver.ResolveDirectoryAsync(skillId, token));

            Assert.Equal(Path.Combine(paths.Root, updated.Skill.ContentPath), resolvedPath);
            Assert.True(Directory.Exists(resolvedPath));
            Assert.Contains(
                "Updated skill instructions",
                await File.ReadAllTextAsync(Path.Combine(resolvedPath, "SKILL.md"), token)
            );
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveDirectoryAsync_LegacyMigrationDatabaseFailure_PreservesOriginalDirectoryAndMetadata()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var userScope = UserInfoUtil.Push(
            new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "skill-migration-failure-test")], "Test")
            )
        );
        var user = new UserInfoService();
        var root = Path.Combine(AppContext.BaseDirectory, ".skill-test-data", Guid.CreateVersion7().ToString("N"));
        var paths = AgwDataPaths.Resolve(root, root);
        paths.EnsureCreated();
        var skillId = Guid.CreateVersion7();
        var oldContentPath = $"skills/{skillId:N}";
        var oldDirectory = Path.Combine(paths.SkillsDirectory, skillId.ToString("N"));
        Directory.CreateDirectory(oldDirectory);
        const string originalMarkdown =
            "---\nname: legacy-skill\ndescription: original description\n---\nOriginal instructions";
        const string originalReference = "Original reference";
        await File.WriteAllTextAsync(Path.Combine(oldDirectory, "SKILL.md"), originalMarkdown, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(oldDirectory, "reference.md"), originalReference, cancellationToken);
        var databasePath = Path.Combine(root, "skills.db");
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite($"Data Source={databasePath};Cache=Shared")
            .Options;

        try
        {
            await using (var seedContext = new AgwDbContext(options))
            {
                await seedContext.Database.EnsureCreatedAsync(cancellationToken);
                seedContext.Skills.Add(
                    new Skill
                    {
                        Id = skillId,
                        Name = "legacy-skill",
                        Description = "original description",
                        Kind = SkillKind.Local,
                        ContentPath = oldContentPath,
                        CreateBy = user.RequiredUserId,
                    }
                );
                await seedContext.SaveChangesAsync(cancellationToken);
            }

            await using var context = new AgwDbContext(options);
            await context.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER reject_skill_content_path_update BEFORE UPDATE OF ContentPath ON skill BEGIN SELECT RAISE(ABORT, 'blocked'); END;",
                cancellationToken
            );
            using var services = CreateProductionServiceProvider();
            var service = CreateProductionSkillService(context, paths, user, services);
            ILocalSkillContentResolver resolver = new LocalSkillContentFacade(service);

            await Assert.ThrowsAsync<DbUpdateException>(() =>
                resolver.ResolveDirectoryAsync(skillId, cancellationToken)
            );

            context.ChangeTracker.Clear();
            var persistedSkill = await context.Skills.SingleAsync(cancellationToken);
            Assert.Equal("legacy-skill", persistedSkill.Name);
            Assert.Equal("original description", persistedSkill.Description);
            Assert.Equal(oldContentPath, persistedSkill.ContentPath);
            Assert.Equal(
                originalMarkdown,
                await File.ReadAllTextAsync(Path.Combine(oldDirectory, "SKILL.md"), cancellationToken)
            );
            Assert.Equal(
                originalReference,
                await File.ReadAllTextAsync(Path.Combine(oldDirectory, "reference.md"), cancellationToken)
            );
            Assert.Equal(oldDirectory, Assert.Single(Directory.EnumerateDirectories(paths.SkillsDirectory)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolveDirectoryAsync_MissingContentDirectory_ReturnsNull(bool legacyLayout)
    {
        var token = TestContext.Current.CancellationToken;
        using var userScope = UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "tester")], "Test"))
        );
        var user = new UserInfoService();
        var root = Path.Combine(AppContext.BaseDirectory, ".skill-test-data", Guid.CreateVersion7().ToString("N"));
        var paths = AgwDataPaths.Resolve(root, root);
        paths.EnsureCreated();
        var skillId = Guid.CreateVersion7();
        var contentPath = legacyLayout
            ? $"skills/{skillId:N}"
            : $"skills/{skillId:N}-{Guid.CreateVersion7():N}/missing-skill";

        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using var context = new AgwDbContext(
                new DbContextOptionsBuilder<AgwDbContext>().UseSqlite(connection).Options
            );
            await context.Database.EnsureCreatedAsync(token);
            context.Skills.Add(
                new Skill
                {
                    Id = skillId,
                    Name = "missing-skill",
                    Description = "description",
                    Kind = SkillKind.Local,
                    ContentPath = contentPath,
                    CreateBy = user.RequiredUserId,
                }
            );
            await context.SaveChangesAsync(token);
            using var services = CreateProductionServiceProvider();
            var service = CreateProductionSkillService(context, paths, user, services);
            ILocalSkillContentResolver resolver = new LocalSkillContentFacade(service);

            Assert.Null(await resolver.ResolveDirectoryAsync(skillId, token));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateAsync_SaveAndConfirmationFail_PreservesOriginalExceptionAndPublishedContent()
    {
        var token = TestContext.Current.CancellationToken;
        using var userScope = UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "tester")], "Test"))
        );
        var root = Path.Combine(AppContext.BaseDirectory, ".skill-test-data", Guid.CreateVersion7().ToString("N"));
        var paths = AgwDataPaths.Resolve(root, root);
        paths.EnsureCreated();
        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
            await connection.OpenAsync(token);
            var disconnect = new DisconnectAfterSaveFailure();
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(disconnect)
                .Options;
            await using var context = new AgwDbContext(options);
            await context.Database.EnsureCreatedAsync(token);
            await context.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER reject_skill_insert BEFORE INSERT ON skill BEGIN SELECT RAISE(ABORT, 'blocked'); END;",
                token
            );
            using var services = CreateProductionServiceProvider();
            var service = CreateProductionSkillService(context, paths, new UserInfoService(), services);
            var skill = new Skill
            {
                Name = "test-skill",
                Description = "description",
                Kind = SkillKind.Local,
                CreateBy = "tester",
            };

            var failure = await Assert.ThrowsAsync<DbUpdateException>(() =>
                service.CreateAsync(skill, CreateArchive(), "tester", cancellationToken: token)
            );

            Assert.Same(disconnect.Failure, failure);
            Assert.True(File.Exists(Path.Combine(root, skill.ContentPath, "SKILL.md")));
            await Assert.ThrowsAsync<SqliteException>(() => context.Skills.AnyAsync(token));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, "name")]
    [InlineData(true, "name")]
    [InlineData(false, "Name")]
    public async Task SaveAsync_LocalArchive_LoadsThroughAgentFileSkillsSource(bool update, string nameKey)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var userScope = UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "tester")], "Test"))
        );
        var root = Path.Combine(AppContext.BaseDirectory, ".skill-test-data", Guid.CreateVersion7().ToString("N"));
        var paths = AgwDataPaths.Resolve(root, root);
        paths.EnsureCreated();
        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(cancellationToken);
            var options = new DbContextOptionsBuilder<AgwDbContext>().UseSqlite(connection).Options;
            await using var context = new AgwDbContext(options);
            await context.Database.EnsureCreatedAsync(cancellationToken);
            using var services = CreateProductionServiceProvider();
            var user = new UserInfoService();
            var service = CreateProductionSkillService(context, paths, user, services);
            var skill = new Skill
            {
                Name = "uploaded-skill",
                Description = "Uploaded instructions",
                Kind = SkillKind.Local,
                CreateBy = user.RequiredUserId,
            };
            var saved = await service.CreateAsync(
                skill,
                CreateArchive("Instructions", nameKey),
                user.RequiredUserId,
                cancellationToken: cancellationToken
            );
            if (update)
            {
                saved = Assert.IsType<SkillDetails>(
                    await service.UpdateAsync(
                        skill.Id,
                        "updated-skill",
                        "Updated instructions",
                        CreateArchive(),
                        user.RequiredUserId,
                        cancellationToken: cancellationToken
                    )
                );
            }

            using var source = new AgentFileSkillsSource(Path.Combine(paths.Root, saved.Skill.ContentPath));
            using var client = new OpenAI.Chat.ChatClient("test-model", "test-key").AsIChatClient();
            var agent = new ChatClientAgent(client);
            var skills = await source.GetSkillsAsync(new AgentSkillsSourceContext(agent, null), cancellationToken);
            var loaded = Assert.Single(skills);
            Assert.Equal(saved.Skill.Name, loaded.Frontmatter.Name);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

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
            var currentUser = new TestCurrentUser("tester");
            var service = new SkillAppService(
                context,
                new TestAgentReferenceFacade(new EfRepository<AgentSkillRelation>(context), context),
                new SkillNameUniquenessDomainService(new SkillRepository(context, currentUser)),
                paths,
                NullLogger<SkillAppService>.Instance,
                remote,
                new RefreshLock(),
                InMemoryApplicationLock.Shared,
                clock,
                currentUser
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
            var expectedPath = kind == SkillKind.Local ? result.Skill.ContentPath : string.Empty;
            Assert.NotEqual(Guid.Empty, id);
            if (kind == SkillKind.Local)
            {
                Assert.StartsWith($"skills/{id:N}-", expectedPath);
            }
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
    public async Task UpdateAsync_LocalArchivePublishFailure_PreservesPreviousContentAndMetadata()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var userScope = UserInfoUtil.Push(
            new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "skill-publish-test")], "Test")
            )
        );
        var user = new UserInfoService();
        var root = Path.Combine(AppContext.BaseDirectory, ".skill-test-data", Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = AgwDataPaths.Resolve(root, root);
        paths.EnsureCreated();
        var skillId = Guid.CreateVersion7();
        var oldContentPath = $"legacy/{skillId:N}";
        var oldDirectory = Path.Combine(paths.Root, oldContentPath);
        Directory.CreateDirectory(oldDirectory);
        var oldSkillMarkdown = Path.Combine(oldDirectory, "SKILL.md");
        const string oldContent = "Previous skill instructions";
        await File.WriteAllTextAsync(oldSkillMarkdown, oldContent, cancellationToken);

        try
        {
            Directory.Delete(paths.SkillsDirectory);
            await File.WriteAllTextAsync(paths.SkillsDirectory, "Publication root obstruction", cancellationToken);

            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(cancellationToken);
            var options = new DbContextOptionsBuilder<AgwDbContext>().UseSqlite(connection).Options;
            await using var context = new AgwDbContext(options);
            await context.Database.EnsureCreatedAsync(cancellationToken);
            context.Skills.Add(
                new Skill
                {
                    Id = skillId,
                    Name = "test-skill",
                    Description = "previous description",
                    Kind = SkillKind.Local,
                    ContentPath = oldContentPath,
                    CreateBy = user.RequiredUserId,
                    CreateTime = TimeProvider.System.GetUtcNow(),
                }
            );
            await context.SaveChangesAsync(cancellationToken);

            using var serviceProvider = CreateProductionServiceProvider();
            var skillService = CreateProductionSkillService(context, paths, user, serviceProvider);

            await Assert.ThrowsAnyAsync<IOException>(() =>
                skillService.UpdateAsync(
                    skillId,
                    "test-skill",
                    "updated description",
                    CreateArchive("Updated skill instructions"),
                    user.RequiredUserId,
                    cancellationToken: cancellationToken
                )
            );

            context.ChangeTracker.Clear();
            var persistedSkill = await context.Skills.SingleAsync(cancellationToken);
            Assert.Equal(oldContentPath, persistedSkill.ContentPath);
            Assert.Equal("previous description", persistedSkill.Description);
            Assert.Equal(oldContent, await File.ReadAllTextAsync(oldSkillMarkdown, cancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateAsync_LocalArchivePublishFailure_DoesNotPersistSkill()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var userScope = UserInfoUtil.Push(
            new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "skill-publish-test")], "Test")
            )
        );
        var user = new UserInfoService();
        var root = Path.Combine(AppContext.BaseDirectory, ".skill-test-data", Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = AgwDataPaths.Resolve(root, root);
        paths.EnsureCreated();
        var skill = new Skill
        {
            Id = Guid.CreateVersion7(),
            Name = "test-skill",
            Description = "description",
            Kind = SkillKind.Local,
        };

        try
        {
            Directory.Delete(paths.SkillsDirectory);
            await File.WriteAllTextAsync(paths.SkillsDirectory, "Publication root obstruction", cancellationToken);

            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(cancellationToken);
            var options = new DbContextOptionsBuilder<AgwDbContext>().UseSqlite(connection).Options;
            await using var context = new AgwDbContext(options);
            await context.Database.EnsureCreatedAsync(cancellationToken);
            using var serviceProvider = CreateProductionServiceProvider();
            var skillService = CreateProductionSkillService(context, paths, user, serviceProvider);

            await Assert.ThrowsAnyAsync<IOException>(() =>
                skillService.CreateAsync(
                    skill,
                    CreateArchive(),
                    user.RequiredUserId,
                    cancellationToken: cancellationToken
                )
            );

            Assert.Equal(string.Empty, skill.ContentPath);
            Assert.Equal(0, await context.Skills.CountAsync(cancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateAsync_LocalArchiveDatabaseFailure_CleansPublishedDirectoryAndDoesNotPersistSkill()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var userScope = UserInfoUtil.Push(
            new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "skill-publish-test")], "Test")
            )
        );
        var user = new UserInfoService();
        var root = Path.Combine(AppContext.BaseDirectory, ".skill-test-data", Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = AgwDataPaths.Resolve(root, root);
        paths.EnsureCreated();
        var observer = new SkillPublicationObserver(paths.Root, null, null);
        var skill = new Skill
        {
            Id = Guid.CreateVersion7(),
            Name = "test-skill",
            Description = "description",
            Kind = SkillKind.Local,
        };

        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(cancellationToken);
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(observer)
                .Options;
            await using var context = new AgwDbContext(options);
            await context.Database.EnsureCreatedAsync(cancellationToken);
            await context.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER reject_skill_insert BEFORE INSERT ON skill BEGIN SELECT RAISE(ABORT, 'blocked'); END;",
                cancellationToken
            );

            using var serviceProvider = CreateProductionServiceProvider();
            var skillService = CreateProductionSkillService(context, paths, user, serviceProvider);
            await Assert.ThrowsAsync<DbUpdateException>(() =>
                skillService.CreateAsync(
                    skill,
                    CreateArchive("Created skill instructions"),
                    user.RequiredUserId,
                    cancellationToken: cancellationToken
                )
            );

            var publishedContentPath = Assert.IsType<string>(observer.ContentPathAtSave);
            Assert.StartsWith($"skills/{skill.Id:N}-", publishedContentPath);
            Assert.Contains("Created skill instructions", Assert.IsType<string>(observer.ContentAtSave));
            Assert.False(Directory.Exists(Path.Combine(paths.Root, publishedContentPath)));
            Assert.Equal(string.Empty, skill.ContentPath);
            Assert.Equal(0, await context.Skills.CountAsync(cancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateAsync_LocalArchivePostSaveFailure_PreservesCommittedDirectory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var userScope = UserInfoUtil.Push(
            new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "skill-publish-test")], "Test")
            )
        );
        var user = new UserInfoService();
        var root = Path.Combine(AppContext.BaseDirectory, ".skill-test-data", Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = AgwDataPaths.Resolve(root, root);
        paths.EnsureCreated();
        var observer = new SkillPublicationObserver(paths.Root, null, null);
        var postSaveFailure = new ThrowAfterSaveInterceptor();
        var auditUser = new AuditUserIdProvider();
        var skill = new Skill
        {
            Id = Guid.CreateVersion7(),
            Name = "test-skill",
            Description = "description",
            Kind = SkillKind.Local,
        };

        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(cancellationToken);
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(
                    new EntityCreatorInterceptor(auditUser, TimeProvider.System),
                    new EntityModifierInterceptor(auditUser, TimeProvider.System),
                    observer,
                    postSaveFailure
                )
                .Options;
            await using var context = new AgwDbContext(options);
            await context.Database.EnsureCreatedAsync(cancellationToken);
            using var serviceProvider = CreateProductionServiceProvider();
            var skillService = CreateProductionSkillService(context, paths, user, serviceProvider);
            postSaveFailure.ThrowOnNextSave = true;

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                skillService.CreateAsync(
                    skill,
                    CreateArchive("Committed skill instructions"),
                    user.RequiredUserId,
                    cancellationToken: cancellationToken
                )
            );

            context.ChangeTracker.Clear();
            var persistedSkill = await context.Skills.SingleAsync(cancellationToken);
            var publishedContentPath = Assert.IsType<string>(persistedSkill.ContentPath);
            Assert.StartsWith($"skills/{skill.Id:N}-", publishedContentPath);
            Assert.Equal(publishedContentPath, observer.ContentPathAtSave);
            Assert.True(File.Exists(Path.Combine(paths.Root, publishedContentPath, "SKILL.md")));
            Assert.Contains(
                "Committed skill instructions",
                await File.ReadAllTextAsync(
                    Path.Combine(paths.Root, publishedContentPath, "SKILL.md"),
                    cancellationToken
                )
            );
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateAsync_LocalArchiveDatabaseFailure_PreservesPreviousVersionAndCleansPublishedDirectory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var userScope = UserInfoUtil.Push(
            new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "skill-publish-test")], "Test")
            )
        );
        var user = new UserInfoService();
        var root = Path.Combine(AppContext.BaseDirectory, ".skill-test-data", Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = AgwDataPaths.Resolve(root, root);
        paths.EnsureCreated();
        var skillId = Guid.CreateVersion7();
        var oldContentPath = $"skills/{skillId:N}";
        var oldDirectory = Path.Combine(paths.Root, oldContentPath);
        Directory.CreateDirectory(oldDirectory);
        var oldSkillMarkdown = Path.Combine(oldDirectory, "SKILL.md");
        const string oldContent = "Previous skill instructions";
        await File.WriteAllTextAsync(oldSkillMarkdown, oldContent, cancellationToken);
        var observer = new SkillPublicationObserver(paths.Root, oldSkillMarkdown, oldContent);

        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(cancellationToken);
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(observer)
                .Options;
            await using var context = new AgwDbContext(options);
            await context.Database.EnsureCreatedAsync(cancellationToken);
            context.Skills.Add(
                new Skill
                {
                    Id = skillId,
                    Name = "test-skill",
                    Description = "previous description",
                    Kind = SkillKind.Local,
                    ContentPath = oldContentPath,
                    CreateBy = user.RequiredUserId,
                    CreateTime = TimeProvider.System.GetUtcNow(),
                }
            );
            await context.SaveChangesAsync(cancellationToken);
            await context.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER reject_skill_update BEFORE UPDATE OF description ON skill BEGIN SELECT RAISE(ABORT, 'blocked'); END;",
                cancellationToken
            );

            using var serviceProvider = CreateProductionServiceProvider();
            var skillService = CreateProductionSkillService(context, paths, user, serviceProvider);
            await Assert.ThrowsAsync<DbUpdateException>(() =>
                skillService.UpdateAsync(
                    skillId,
                    "test-skill",
                    "updated description",
                    CreateArchive("Updated skill instructions"),
                    user.RequiredUserId,
                    cancellationToken: cancellationToken
                )
            );

            var publishedContentPath = Assert.IsType<string>(observer.ContentPathAtSave);
            Assert.NotEqual(oldContentPath, publishedContentPath);
            Assert.True(observer.OldContentExistsAtSave);
            Assert.Contains("Updated skill instructions", Assert.IsType<string>(observer.ContentAtSave));
            Assert.False(Directory.Exists(Path.Combine(paths.Root, publishedContentPath)));

            context.ChangeTracker.Clear();
            var persistedSkill = await context.Skills.SingleAsync(cancellationToken);
            Assert.Equal(oldContentPath, persistedSkill.ContentPath);
            Assert.Equal("previous description", persistedSkill.Description);
            Assert.Equal(oldContent, await File.ReadAllTextAsync(oldSkillMarkdown, cancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateAsync_LocalArchivePostSaveFailure_PreservesCommittedVersionAndCleansPreviousDirectory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var userScope = UserInfoUtil.Push(
            new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "skill-publish-test")], "Test")
            )
        );
        var user = new UserInfoService();
        var root = Path.Combine(AppContext.BaseDirectory, ".skill-test-data", Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = AgwDataPaths.Resolve(root, root);
        paths.EnsureCreated();
        var skillId = Guid.CreateVersion7();
        var oldContentPath = $"skills/{skillId:N}";
        var oldDirectory = Path.Combine(paths.Root, oldContentPath);
        Directory.CreateDirectory(oldDirectory);
        var oldSkillMarkdown = Path.Combine(oldDirectory, "SKILL.md");
        const string oldContent = "Previous skill instructions";
        await File.WriteAllTextAsync(oldSkillMarkdown, oldContent, cancellationToken);
        var observer = new SkillPublicationObserver(paths.Root, oldSkillMarkdown, oldContent);
        var postSaveFailure = new ThrowAfterSaveInterceptor();
        var auditUser = new AuditUserIdProvider();

        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(cancellationToken);
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(
                    new EntityCreatorInterceptor(auditUser, TimeProvider.System),
                    new EntityModifierInterceptor(auditUser, TimeProvider.System),
                    observer,
                    postSaveFailure
                )
                .Options;
            await using var context = new AgwDbContext(options);
            await context.Database.EnsureCreatedAsync(cancellationToken);
            context.Skills.Add(
                new Skill
                {
                    Id = skillId,
                    Name = "test-skill",
                    Description = "previous description",
                    Kind = SkillKind.Local,
                    ContentPath = oldContentPath,
                    CreateBy = user.RequiredUserId,
                    CreateTime = TimeProvider.System.GetUtcNow(),
                }
            );
            await context.SaveChangesAsync(cancellationToken);

            using var serviceProvider = CreateProductionServiceProvider();
            var skillService = CreateProductionSkillService(context, paths, user, serviceProvider);
            postSaveFailure.ThrowOnNextSave = true;
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                skillService.UpdateAsync(
                    skillId,
                    "test-skill",
                    "committed description",
                    CreateArchive("Committed skill instructions"),
                    user.RequiredUserId,
                    cancellationToken: cancellationToken
                )
            );

            var publishedContentPath = Assert.IsType<string>(observer.ContentPathAtSave);
            Assert.NotEqual(oldContentPath, publishedContentPath);
            Assert.True(observer.OldContentExistsAtSave);
            Assert.False(Directory.Exists(oldDirectory));
            Assert.True(File.Exists(Path.Combine(paths.Root, publishedContentPath, "SKILL.md")));

            context.ChangeTracker.Clear();
            var persistedSkill = await context.Skills.SingleAsync(cancellationToken);
            Assert.Equal(publishedContentPath, persistedSkill.ContentPath);
            Assert.Equal("committed description", persistedSkill.Description);
            Assert.Contains(
                "Committed skill instructions",
                await File.ReadAllTextAsync(
                    Path.Combine(paths.Root, publishedContentPath, "SKILL.md"),
                    cancellationToken
                )
            );
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateAsync_LocalArchive_PublishesNewVersionBeforeSavingAndRemovesPreviousDirectory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var userScope = UserInfoUtil.Push(
            new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "skill-publish-test")], "Test")
            )
        );
        var user = new UserInfoService();
        var root = Path.Combine(AppContext.BaseDirectory, ".skill-test-data", Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = AgwDataPaths.Resolve(root, root);
        paths.EnsureCreated();
        var skillId = Guid.CreateVersion7();
        var oldContentPath = $"skills/{skillId:N}";
        var oldDirectory = Path.Combine(paths.Root, oldContentPath);
        Directory.CreateDirectory(oldDirectory);
        var oldSkillMarkdown = Path.Combine(oldDirectory, "SKILL.md");
        const string oldContent = "Previous skill instructions";
        await File.WriteAllTextAsync(oldSkillMarkdown, oldContent, cancellationToken);
        var observer = new SkillPublicationObserver(paths.Root, oldSkillMarkdown, oldContent);

        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(cancellationToken);
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(observer)
                .Options;
            await using var context = new AgwDbContext(options);
            await context.Database.EnsureCreatedAsync(cancellationToken);
            context.Skills.Add(
                new Skill
                {
                    Id = skillId,
                    Name = "test-skill",
                    Description = "previous description",
                    Kind = SkillKind.Local,
                    ContentPath = oldContentPath,
                    CreateBy = user.RequiredUserId,
                    CreateTime = TimeProvider.System.GetUtcNow(),
                }
            );
            await context.SaveChangesAsync(cancellationToken);

            using var serviceProvider = CreateProductionServiceProvider();
            var skillService = CreateProductionSkillService(context, paths, user, serviceProvider);
            var result = await skillService.UpdateAsync(
                skillId,
                "test-skill",
                "updated description",
                CreateArchive("Updated skill instructions"),
                user.RequiredUserId,
                cancellationToken: cancellationToken
            );

            var publishedContentPath = Assert.IsType<string>(observer.ContentPathAtSave);
            Assert.NotEqual(oldContentPath, publishedContentPath);
            Assert.True(observer.OldContentExistsAtSave);
            Assert.Contains("Updated skill instructions", Assert.IsType<string>(observer.ContentAtSave));
            Assert.Equal(publishedContentPath, result!.Skill.ContentPath);
            Assert.True(File.Exists(Path.Combine(paths.Root, publishedContentPath, "SKILL.md")));
            Assert.False(Directory.Exists(oldDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateAndDelete_IndependentContexts_UseSameSkillMutationLock()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var userScope = UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "skill-lock-test")], "Test"))
        );
        var user = new UserInfoService();
        var root = Path.Combine(AppContext.BaseDirectory, ".skill-test-data", Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = AgwDataPaths.Resolve(root, root);
        paths.EnsureCreated();
        var skillId = Guid.CreateVersion7();
        var contentPath = $"skills/{skillId:N}";
        var contentDirectory = Path.Combine(paths.Root, contentPath);
        Directory.CreateDirectory(contentDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(contentDirectory, "SKILL.md"),
            "Skill instructions",
            cancellationToken
        );
        var databasePath = Path.Combine(root, "skills.db");
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite($"Data Source={databasePath};Cache=Shared")
            .Options;

        try
        {
            await using (var seedContext = new AgwDbContext(options))
            {
                await seedContext.Database.EnsureCreatedAsync(cancellationToken);
                seedContext.Skills.Add(
                    new Skill
                    {
                        Id = skillId,
                        Name = "test-skill",
                        Description = "description",
                        Kind = SkillKind.Local,
                        ContentPath = contentPath,
                        CreateBy = user.RequiredUserId,
                        CreateTime = TimeProvider.System.GetUtcNow(),
                    }
                );
                await seedContext.SaveChangesAsync(cancellationToken);
            }

            using var serviceProvider = CreateProductionServiceProvider();
            await using var updateContext = new AgwDbContext(options);
            await using var deleteContext = new AgwDbContext(options);
            Assert.NotSame(updateContext, deleteContext);
            var updateService = CreateProductionSkillService(updateContext, paths, user, serviceProvider);
            var deleteService = CreateProductionSkillService(deleteContext, paths, user, serviceProvider);
            Task<SkillDetails?> updateTask;
            Task<bool> deleteTask;
            bool updateWasBlocked;
            bool deleteWasBlocked;

            await using (
                var heldLease = await InMemoryApplicationLock.Shared.AcquireAsync(
                    SkillMutationLock.GetResourceName(skillId),
                    cancellationToken
                )
            )
            {
                updateTask = updateService.UpdateAsync(
                    skillId,
                    "test-skill",
                    "updated description",
                    null,
                    user.RequiredUserId,
                    cancellationToken: cancellationToken
                );
                deleteTask = deleteService.DeleteAsync(skillId, cancellationToken);
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                updateWasBlocked = !updateTask.IsCompleted;
                deleteWasBlocked = !deleteTask.IsCompleted;
            }

            await Task.WhenAll(updateTask, deleteTask);

            Assert.True(updateWasBlocked);
            Assert.True(deleteWasBlocked);
            Assert.True(await deleteTask);
            await using var verifyContext = new AgwDbContext(options);
            Assert.Equal(0, await verifyContext.Skills.CountAsync(cancellationToken));
            Assert.False(Directory.Exists(contentDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveDirectoryAsync_ConcurrentDelete_ReturnsNullAfterDeleteGetsMutationLock()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var userScope = UserInfoUtil.Push(
            new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "skill-runtime-delete-test")], "Test")
            )
        );
        var user = new UserInfoService();
        var root = Path.Combine(AppContext.BaseDirectory, ".skill-test-data", Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = AgwDataPaths.Resolve(root, root);
        paths.EnsureCreated();
        var skillId = Guid.CreateVersion7();
        var contentPath = $"skills/{skillId:N}";
        var contentDirectory = Path.Combine(paths.SkillsDirectory, skillId.ToString("N"));
        Directory.CreateDirectory(contentDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(contentDirectory, "SKILL.md"),
            "Skill instructions",
            cancellationToken
        );
        var databasePath = Path.Combine(root, "skills.db");
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite($"Data Source={databasePath};Cache=Shared")
            .Options;

        try
        {
            await using (var seedContext = new AgwDbContext(options))
            {
                await seedContext.Database.EnsureCreatedAsync(cancellationToken);
                seedContext.Skills.Add(
                    new Skill
                    {
                        Id = skillId,
                        Name = "test-skill",
                        Description = "description",
                        Kind = SkillKind.Local,
                        ContentPath = contentPath,
                        CreateBy = user.RequiredUserId,
                        CreateTime = TimeProvider.System.GetUtcNow(),
                    }
                );
                await seedContext.SaveChangesAsync(cancellationToken);
            }

            using var services = CreateProductionServiceProvider();
            await using var deleteContext = new AgwDbContext(options);
            await using var resolveContext = new AgwDbContext(options);
            var deleteService = CreateProductionSkillService(deleteContext, paths, user, services);
            var resolveService = CreateProductionSkillService(resolveContext, paths, user, services);
            ILocalSkillContentResolver resolver = new LocalSkillContentFacade(resolveService);
            Task<bool> deleteTask;
            Task<string?> resolveTask;
            bool deleteWasBlocked;
            bool resolveWasBlocked;

            await using (
                var heldLease = await InMemoryApplicationLock.Shared.AcquireAsync(
                    SkillMutationLock.GetResourceName(skillId),
                    cancellationToken
                )
            )
            {
                deleteTask = deleteService.DeleteAsync(skillId, cancellationToken);
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                resolveTask = resolver.ResolveDirectoryAsync(skillId, cancellationToken);
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                deleteWasBlocked = !deleteTask.IsCompleted;
                resolveWasBlocked = !resolveTask.IsCompleted;
            }

            await Task.WhenAll(deleteTask, resolveTask);

            Assert.True(deleteWasBlocked);
            Assert.True(resolveWasBlocked);
            Assert.True(await deleteTask);
            Assert.Null(await resolveTask);
            Assert.False(Directory.Exists(contentDirectory));
            await using var verifyContext = new AgwDbContext(options);
            Assert.Equal(0, await verifyContext.Skills.CountAsync(cancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateAsync_LocalArchiveNameWithWhitespaceBeforeColon_PreservesNestedMetadataForMaf()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var userScope = UserInfoUtil.Push(
            new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "skill-markdown-whitespace-test")], "Test")
            )
        );
        var user = new UserInfoService();
        var root = Path.Combine(AppContext.BaseDirectory, ".skill-test-data", Guid.CreateVersion7().ToString("N"));
        var paths = AgwDataPaths.Resolve(root, root);
        paths.EnsureCreated();

        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(cancellationToken);
            await using var context = new AgwDbContext(
                new DbContextOptionsBuilder<AgwDbContext>().UseSqlite(connection).Options
            );
            await context.Database.EnsureCreatedAsync(cancellationToken);
            using var services = CreateProductionServiceProvider();
            var service = CreateProductionSkillService(context, paths, user, services);
            var created = await service.CreateAsync(
                new Skill
                {
                    Name = "test-skill",
                    Description = "description",
                    Kind = SkillKind.Local,
                },
                CreateArchive("Instructions", "name ", "imported-name", "metadata:\n  name: author"),
                user.RequiredUserId,
                cancellationToken: cancellationToken
            );
            using var source = new AgentFileSkillsSource(Path.Combine(paths.Root, created.Skill.ContentPath));
            using var client = new OpenAI.Chat.ChatClient("test-model", "test-key").AsIChatClient();
            var skills = await source.GetSkillsAsync(
                new AgentSkillsSourceContext(new ChatClientAgent(client), null),
                cancellationToken
            );
            var skillMarkdown = (
                await File.ReadAllTextAsync(
                    Path.Combine(paths.Root, created.Skill.ContentPath, "SKILL.md"),
                    cancellationToken
                )
            ).Replace("\r\n", "\n", StringComparison.Ordinal);

            Assert.Equal("test-skill", Assert.Single(skills).Frontmatter.Name);
            Assert.Contains("\nname: test-skill\nmetadata:\n  name: author\ndescription:", skillMarkdown);
            Assert.Equal(
                1,
                skillMarkdown.Split('\n').Count(line => line.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
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
            null!,
            NullLogger<SkillAppService>.Instance,
            null!,
            null!,
            InMemoryApplicationLock.Shared,
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

    private static IFormFile CreateArchive(
        string instructions,
        string nameKey = "name",
        string nameValue = "test-skill",
        string? additionalFrontmatter = null
    )
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry("test-skill/SKILL.md").Open()))
        {
            var additionalFrontmatterLines = additionalFrontmatter is null
                ? string.Empty
                : $"{additionalFrontmatter}\n";
            writer.Write(
                $"---\n{nameKey}: {nameValue}\n{additionalFrontmatterLines}description: description\n---\n{instructions}"
            );
        }
        stream.Position = 0;
        return new FormFile(stream, 0, stream.Length, "archive", "skill.zip");
    }

    private static ServiceProvider CreateProductionServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddHttpClient();
        return services.BuildServiceProvider();
    }

    private static SkillAppService CreateProductionSkillService(
        AgwDbContext context,
        AgwDataPaths paths,
        UserInfoService user,
        IServiceProvider serviceProvider
    )
    {
        var remoteSkillRefreshLock = new RemoteSkillRefreshLockRouter(
            serviceProvider.GetRequiredService<IOptionsMonitor<DatabaseSettings>>(),
            serviceProvider.GetRequiredService<IOptionsMonitor<DistributedLockSettings>>(),
            (_, connectionString) => new PostgresDistributedSynchronizationProvider(connectionString),
            NullLogger<RemoteSkillRefreshLockRouter>.Instance
        );
        return new SkillAppService(
            context,
            new AgentCatalogFacade(context, user),
            new SkillNameUniquenessDomainService(new SkillRepository(context, user)),
            paths,
            NullLogger<SkillAppService>.Instance,
            new RemoteSkillHttpClient(serviceProvider.GetRequiredService<IHttpClientFactory>()),
            remoteSkillRefreshLock,
            InMemoryApplicationLock.Shared,
            TimeProvider.System,
            user
        );
    }

    private sealed class AuditUserIdProvider : IEntityAuditUserIdProvider
    {
        public string GetUserId() => UserInfoUtil.RequiredUserId;
    }

    private sealed class SkillPublicationObserver : SaveChangesInterceptor
    {
        private readonly string _rootPath;
        private readonly string? _oldSkillMarkdown;
        private readonly string? _oldContent;

        public SkillPublicationObserver(string rootPath, string? oldSkillMarkdown, string? oldContent)
        {
            _rootPath = rootPath;
            _oldSkillMarkdown = oldSkillMarkdown;
            _oldContent = oldContent;
        }

        public string? ContentPathAtSave { get; private set; }

        public string? ContentAtSave { get; private set; }

        public bool OldContentExistsAtSave { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
        {
            var skill = eventData
                .Context?.ChangeTracker.Entries<Skill>()
                .SingleOrDefault(entry => entry.State is EntityState.Added or EntityState.Modified)
                ?.Entity;
            if (skill is not null)
            {
                ContentPathAtSave = skill.ContentPath;
                var skillMarkdown = Path.Combine(_rootPath, skill.ContentPath, "SKILL.md");
                ContentAtSave = File.Exists(skillMarkdown) ? File.ReadAllText(skillMarkdown) : null;
                OldContentExistsAtSave =
                    _oldSkillMarkdown is not null
                    && _oldContent is not null
                    && File.Exists(_oldSkillMarkdown)
                    && string.Equals(File.ReadAllText(_oldSkillMarkdown), _oldContent, StringComparison.Ordinal);
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class DisconnectAfterSaveFailure : SaveChangesInterceptor
    {
        public Exception? Failure { get; private set; }

        public override async Task SaveChangesFailedAsync(
            DbContextErrorEventData eventData,
            CancellationToken cancellationToken = default
        )
        {
            Failure = eventData.Exception;
            await eventData.Context!.Database.GetDbConnection().CloseAsync();
        }
    }

    private sealed class ThrowAfterSaveInterceptor : SaveChangesInterceptor
    {
        public bool ThrowOnNextSave { get; set; }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default
        )
        {
            if (ThrowOnNextSave)
            {
                ThrowOnNextSave = false;
                throw new InvalidOperationException("Post-save callback failure");
            }

            return base.SavedChangesAsync(eventData, result, cancellationToken);
        }
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
