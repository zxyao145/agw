using System.Linq.Expressions;

using Agw.Agents.Execution.Agents.Skills;
using Agw.Domain.Services.Skills;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Agw.Skills.Application;
using Agw.Testing;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Skills.Tests;

public class SkillAppServiceRemoteTests
{
    private static readonly DateTimeOffset UtcNow =
        new(2026, 7, 26, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateAsync_RemoteSkill_UsesRemoteMetadataAndStoresSnapshot()
    {
        await using var fixture = new TestFixture(
            new RemoteSkillDefinition(
                "expense-report",
                "Enterprise expense policy",
                "Complete skill instructions.",
                ["finance", "policy"]));

        var result = await fixture.Service.CreateAsync(
            new Skill { Kind = SkillKind.Remote },
            archive: null,
            "remote-admin",
            "https://example.com/skills/expense-report",
            TestContext.Current.CancellationToken);

        Assert.Equal("expense-report", result.Skill.Name);
        Assert.Equal("Enterprise expense policy", result.Skill.Description);
        Assert.Equal(SkillKind.Remote, result.Skill.Kind);
        Assert.Equal(string.Empty, result.Skill.ContentPath);
        Assert.Equal(
            "https://example.com/skills/expense-report",
            result.Skill.RemoteUrl);
        Assert.Equal("remote-admin", result.Skill.CreateBy);
        Assert.Equal(UtcNow, result.Skill.CreateTime);
        var cache = Assert.Single(fixture.CacheRepository.Items);
        Assert.Equal(result.Skill.Id, cache.SkillId);
        Assert.Equal(result.Skill.RemoteUrl, cache.SourceUrl);
        Assert.Equal(UtcNow, cache.FetchedAt);
        Assert.Equal(
            "Complete skill instructions.",
            RemoteSkillDefinitionSerializer.Deserialize(cache.ContentJson)?.Instructions);
    }

    [Fact]
    public async Task CreateAsync_RemoteSkillWithArchive_RejectsArchive()
    {
        await using var fixture = new TestFixture(CreateDefinition());
        await using var stream = new MemoryStream([1, 2, 3]);
        var archive = new FormFile(stream, 0, stream.Length, "archive", "skill.zip");

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.CreateAsync(
                new Skill { Kind = SkillKind.Remote },
                archive,
                "remote-admin",
                "https://example.com/skill",
                TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.RemoteSkillArchiveNotAllowed.Code, exception.Code);
        Assert.Empty(fixture.SkillRepository.Items);
        Assert.Empty(fixture.CacheRepository.Items);
    }

    [Fact]
    public async Task CreateAsync_BuiltInSkill_RejectsApiCreation()
    {
        await using var fixture = new TestFixture(CreateDefinition());

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.CreateAsync(
                new Skill { Kind = SkillKind.BuiltIn },
                archive: null,
                "remote-admin",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.SkillKindInvalid.Code, exception.Code);
    }

    [Fact]
    public async Task UpdateAsync_RemoteSkill_RefreshesUrlMetadataAndSnapshot()
    {
        var skill = new Skill
        {
            Id = Guid.CreateVersion7(),
            Name = "expense-report",
            Description = "Old description",
            Kind = SkillKind.Remote,
            RemoteUrl = "https://old.example.com/skill",
            ContentPath = string.Empty,
        };
        var cache = new RemoteSkillCache
        {
            SkillId = skill.Id,
            SourceUrl = skill.RemoteUrl,
            ContentJson = RemoteSkillDefinitionSerializer.Serialize(
                new RemoteSkillDefinition(
                    skill.Name,
                    skill.Description,
                    "old instructions",
                    [])),
            FetchedAt = UtcNow.AddHours(-1),
        };
        var refreshed = new RemoteSkillDefinition(
            "renamed-expense-report",
            "Updated description",
            "updated instructions",
            ["finance"]);
        await using var fixture = new TestFixture(refreshed, [skill], [cache]);

        var result = await fixture.Service.UpdateAsync(
            skill.Id,
            name: string.Empty,
            description: string.Empty,
            archive: null,
            "remote-admin",
            "https://new.example.com/skill",
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("renamed-expense-report", result.Skill.Name);
        Assert.Equal("Updated description", result.Skill.Description);
        Assert.Equal("https://new.example.com/skill", result.Skill.RemoteUrl);
        Assert.Equal(SkillKind.Remote, result.Skill.Kind);
        Assert.Equal("remote-admin", result.Skill.UpdateBy);
        Assert.Equal(UtcNow, result.Skill.UpdateTime);
        Assert.Equal(1, fixture.RefreshLock.AcquireCount);
        Assert.Equal(result.Skill.RemoteUrl, cache.SourceUrl);
        Assert.Equal(
            "updated instructions",
            RemoteSkillDefinitionSerializer.Deserialize(cache.ContentJson)?.Instructions);
    }

    [Fact]
    public async Task UpdateAsync_LocalSkillWithRemoteUrl_RejectsKindSwitch()
    {
        var skill = new Skill
        {
            Id = Guid.CreateVersion7(),
            Name = "local-skill",
            Description = "Local description",
            Kind = SkillKind.Local,
            ContentPath = "skills/local-skill",
        };
        await using var fixture = new TestFixture(CreateDefinition(), [skill]);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.UpdateAsync(
                skill.Id,
                skill.Name,
                skill.Description,
                archive: null,
                "local-admin",
                "https://example.com/remote",
                TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.SkillKindInvalid.Code, exception.Code);
        Assert.Equal(SkillKind.Local, skill.Kind);
    }

    [Fact]
    public async Task DeleteAsync_RemoteSkill_RemovesSharedSnapshotWithoutFileDeletion()
    {
        var skill = new Skill
        {
            Id = Guid.CreateVersion7(),
            Name = "expense-report",
            Description = "Remote description",
            Kind = SkillKind.Remote,
            RemoteUrl = "https://example.com/skill",
            ContentPath = string.Empty,
        };
        var cache = new RemoteSkillCache
        {
            SkillId = skill.Id,
            SourceUrl = skill.RemoteUrl,
            ContentJson = RemoteSkillDefinitionSerializer.Serialize(CreateDefinition()),
            FetchedAt = UtcNow,
        };
        await using var fixture = new TestFixture(CreateDefinition(), [skill], [cache]);

        var deleted = await fixture.Service.DeleteAsync(
            skill.Id,
            TestContext.Current.CancellationToken);

        Assert.True(deleted);
        Assert.Empty(fixture.SkillRepository.Items);
        Assert.Empty(fixture.CacheRepository.Items);
        Assert.Equal(1, fixture.RefreshLock.AcquireCount);
    }

    private static RemoteSkillDefinition CreateDefinition()
    {
        return new RemoteSkillDefinition(
            "expense-report",
            "Remote description",
            "instructions",
            []);
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        private readonly string _root;

        public TestFixture(
            RemoteSkillDefinition definition,
            IEnumerable<Skill>? skills = null,
            IEnumerable<RemoteSkillCache>? caches = null)
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                $"agw-remote-skill-service-{Guid.CreateVersion7():N}");
            var dataPaths = AgwDataPaths.Resolve(_root, "/unused");
            dataPaths.EnsureCreated();
            SkillRepository = new TestRepository<Skill>(
                skills ?? [],
                entity => entity.Id);
            CacheRepository = new TestRepository<RemoteSkillCache>(
                caches ?? [],
                entity => entity.SkillId);
            RefreshLock = new TestRemoteSkillRefreshLock();
            Service = new SkillAppService(
                SkillRepository,
                new TestRepository<Agent>([], entity => entity.Id),
                new TestRepository<AgentSkillRelation>([], _ => Guid.Empty),
                CacheRepository,
                new TestUnitOfWork(),
                new SkillDomainService(new TestTimeProvider(UtcNow)),
                dataPaths,
                NullLogger<SkillAppService>.Instance,
                new TestRemoteSkillClient(definition),
                RefreshLock,
                new TestTimeProvider(UtcNow));
        }

        public SkillAppService Service { get; }

        public TestRepository<Skill> SkillRepository { get; }

        public TestRepository<RemoteSkillCache> CacheRepository { get; }

        public TestRemoteSkillRefreshLock RefreshLock { get; }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(_root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestRemoteSkillClient : IRemoteSkillClient
    {
        private readonly RemoteSkillDefinition _definition;

        public TestRemoteSkillClient(RemoteSkillDefinition definition)
        {
            _definition = definition;
        }

        public string NormalizeUrl(string? remoteUrl)
        {
            if (string.IsNullOrWhiteSpace(remoteUrl))
            {
                throw new AgwException(ErrorCodes.RemoteSkillUrlRequired);
            }

            if (!Uri.TryCreate(remoteUrl.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new AgwException(ErrorCodes.RemoteSkillUrlInvalid);
            }

            return uri.AbsoluteUri;
        }

        public Task<RemoteSkillDefinition> FetchAsync(
            string remoteUrl,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_definition);
    }

    private sealed class TestRemoteSkillRefreshLock : IRemoteSkillRefreshLock
    {
        public int AcquireCount { get; private set; }

        public Task<IAsyncDisposable> AcquireAsync(
            Guid skillId,
            CancellationToken cancellationToken)
        {
            AcquireCount++;
            return Task.FromResult<IAsyncDisposable>(new TestLease());
        }

        private sealed class TestLease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    public sealed class TestRepository<TEntity> : IRepository<TEntity>
        where TEntity : class
    {
        private readonly Func<TEntity, Guid> _idSelector;

        public TestRepository(
            IEnumerable<TEntity> items,
            Func<TEntity, Guid> idSelector)
        {
            Items = items.ToList();
            _idSelector = idSelector;
        }

        public List<TEntity> Items { get; }

        public IQueryable<TEntity> Queryable => Items.AsQueryable();

        public Task<TEntity?> GetByIdAsync(object id)
        {
            var typedId = Assert.IsType<Guid>(id);
            return Task.FromResult(
                Items.SingleOrDefault(item => _idSelector(item) == typedId));
        }

        public Task<TEntity?> SingleOrDefaultAsync(
            Expression<Func<TEntity, bool>> predicate,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.AsQueryable().SingleOrDefault(predicate));

        public Task<IReadOnlyList<TEntity>> ListAsync(
            Expression<Func<TEntity, bool>>? predicate = null,
            Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null)
        {
            IQueryable<TEntity> query = Items.AsQueryable();
            if (predicate != null)
            {
                query = query.Where(predicate);
            }

            if (orderBy != null)
            {
                query = orderBy(query);
            }

            return Task.FromResult<IReadOnlyList<TEntity>>(query.ToList());
        }

        public Task<IReadOnlyList<TEntity>> ListAsync(
            Expression<Func<TEntity, bool>>? predicate,
            Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy,
            params Expression<Func<TEntity, object>>[] includes) =>
            ListAsync(predicate, orderBy);

        public Task AddAsync(TEntity entity)
        {
            Items.Add(entity);
            return Task.CompletedTask;
        }

        public void Update(TEntity entity)
        {
        }

        public void Remove(TEntity entity)
        {
            Items.Remove(entity);
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class TestUnitOfWork : IUnitOfWork
    {
        public void Dispose()
        {
        }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<bool> SaveEntitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
