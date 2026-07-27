using System.Collections.Concurrent;

using Agw.Agents.Execution.Agents.Skills;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
using Agw.Testing;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public class RemoteSkillContentResolverTests
{
    private static readonly DateTimeOffset UtcNow =
        new(2026, 7, 26, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ResolveAsync_FreshSharedSnapshot_DoesNotFetchRemote()
    {
        await using var database = await TestDatabase.CreateAsync();
        var skill = CreateRemoteSkill();
        var cached = CreateDefinition("cached instructions");
        await database.SeedAsync(
            skill,
            new RemoteSkillCache
            {
                SkillId = skill.Id,
                SourceUrl = skill.RemoteUrl!,
                ContentJson = RemoteSkillDefinitionSerializer.Serialize(cached),
                FetchedAt = UtcNow,
            });
        var client = new TestRemoteSkillClient(CreateDefinition("remote instructions"));
        var timeProvider = new TestTimeProvider(UtcNow.AddMinutes(59));
        await using var node = database.CreateNode();
        var resolver = CreateResolver(node, client, new TestRefreshLock(), timeProvider);

        var result = await resolver.ResolveAsync(
            skill.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal("cached instructions", result.Instructions);
        Assert.Equal(0, client.FetchCount);
    }

    [Fact]
    public async Task ResolveAsync_AtOneHour_RefreshesAndUpdatesSharedSnapshot()
    {
        await using var database = await TestDatabase.CreateAsync();
        var skill = CreateRemoteSkill();
        await database.SeedAsync(
            skill,
            new RemoteSkillCache
            {
                SkillId = skill.Id,
                SourceUrl = skill.RemoteUrl!,
                ContentJson = RemoteSkillDefinitionSerializer.Serialize(
                    CreateDefinition("expired instructions")),
                FetchedAt = UtcNow,
            });
        var refreshed = CreateDefinition("refreshed instructions");
        var client = new TestRemoteSkillClient(refreshed);
        var timeProvider = new TestTimeProvider(UtcNow.AddHours(1));
        await using var node = database.CreateNode();
        var resolver = CreateResolver(node, client, new TestRefreshLock(), timeProvider);

        var result = await resolver.ResolveAsync(
            skill.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal("refreshed instructions", result.Instructions);
        Assert.Equal(1, client.FetchCount);
        await using var verificationScope = node.CreateAsyncScope();
        var cache = await verificationScope.ServiceProvider
            .GetRequiredService<AgwDbContext>()
            .RemoteSkillCaches
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(timeProvider.GetUtcNow(), cache.FetchedAt);
        Assert.Equal(
            "refreshed instructions",
            RemoteSkillDefinitionSerializer.Deserialize(cache.ContentJson)?.Instructions);
    }

    [Fact]
    public async Task ResolveAsync_TwoNodesConcurrentRefresh_PerformsOneRemoteGet()
    {
        await using var database = await TestDatabase.CreateAsync();
        var skill = CreateRemoteSkill();
        await database.SeedAsync(
            skill,
            new RemoteSkillCache
            {
                SkillId = skill.Id,
                SourceUrl = skill.RemoteUrl!,
                ContentJson = RemoteSkillDefinitionSerializer.Serialize(
                    CreateDefinition("expired instructions")),
                FetchedAt = UtcNow.AddHours(-2),
            });
        var client = new TestRemoteSkillClient(
            CreateDefinition("shared refreshed instructions"),
            TimeSpan.FromMilliseconds(50));
        var refreshLock = new TestRefreshLock();
        var timeProvider = new TestTimeProvider(UtcNow);
        await using var firstNode = database.CreateNode();
        await using var secondNode = database.CreateNode();
        var firstResolver = CreateResolver(firstNode, client, refreshLock, timeProvider);
        var secondResolver = CreateResolver(secondNode, client, refreshLock, timeProvider);

        var results = await Task.WhenAll(
            firstResolver.ResolveAsync(skill.Id, TestContext.Current.CancellationToken),
            secondResolver.ResolveAsync(skill.Id, TestContext.Current.CancellationToken));

        Assert.All(
            results,
            result => Assert.Equal("shared refreshed instructions", result.Instructions));
        Assert.Equal(1, client.FetchCount);
    }

    [Fact]
    public async Task ResolveAsync_RefreshFails_DoesNotExtendExpiredSnapshot()
    {
        await using var database = await TestDatabase.CreateAsync();
        var skill = CreateRemoteSkill();
        var fetchedAt = UtcNow.AddHours(-2);
        await database.SeedAsync(
            skill,
            new RemoteSkillCache
            {
                SkillId = skill.Id,
                SourceUrl = skill.RemoteUrl!,
                ContentJson = RemoteSkillDefinitionSerializer.Serialize(
                    CreateDefinition("expired instructions")),
                FetchedAt = fetchedAt,
            });
        var client = new TestRemoteSkillClient(
            new AgwException(ErrorCodes.RemoteSkillFetchFailed));
        await using var node = database.CreateNode();
        var resolver = CreateResolver(
            node,
            client,
            new TestRefreshLock(),
            new TestTimeProvider(UtcNow));

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            resolver.ResolveAsync(skill.Id, TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.RemoteSkillFetchFailed.Code, exception.Code);
        await using var verificationScope = node.CreateAsyncScope();
        var cache = await verificationScope.ServiceProvider
            .GetRequiredService<AgwDbContext>()
            .RemoteSkillCaches
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(fetchedAt, cache.FetchedAt);
    }

    [Fact]
    public async Task ResolveAsync_RemoteNameChanged_RejectsIdentityDrift()
    {
        await using var database = await TestDatabase.CreateAsync();
        var skill = CreateRemoteSkill();
        await database.SeedAsync(skill);
        var changed = new RemoteSkillDefinition(
            "renamed-skill",
            "Remote description",
            "instructions",
            []);
        await using var node = database.CreateNode();
        var resolver = CreateResolver(
            node,
            new TestRemoteSkillClient(changed),
            new TestRefreshLock(),
            new TestTimeProvider(UtcNow));

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            resolver.ResolveAsync(skill.Id, TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.RemoteSkillIdentityChanged.Code, exception.Code);
    }

    private static RemoteSkillContentResolver CreateResolver(
        ServiceProvider node,
        IRemoteSkillClient client,
        IRemoteSkillRefreshLock refreshLock,
        TimeProvider timeProvider)
    {
        return new RemoteSkillContentResolver(
            node.GetRequiredService<IServiceScopeFactory>(),
            client,
            refreshLock,
            timeProvider,
            NullLogger<RemoteSkillContentResolver>.Instance);
    }

    private static Skill CreateRemoteSkill()
    {
        return new Skill
        {
            Id = Guid.CreateVersion7(),
            Name = "expense-report",
            Description = "Remote description",
            Kind = SkillKind.Remote,
            ContentPath = string.Empty,
            RemoteUrl = "https://example.com/skills/expense-report",
            CreateTime = UtcNow,
        };
    }

    private static RemoteSkillDefinition CreateDefinition(string instructions)
    {
        return new RemoteSkillDefinition(
            "expense-report",
            "Remote description",
            instructions,
            ["finance"]);
    }

    private sealed class TestRemoteSkillClient : IRemoteSkillClient
    {
        private readonly RemoteSkillDefinition? _definition;
        private readonly Exception? _exception;
        private readonly TimeSpan _delay;
        private int _fetchCount;

        public TestRemoteSkillClient(
            RemoteSkillDefinition definition,
            TimeSpan delay = default)
        {
            _definition = definition;
            _delay = delay;
        }

        public TestRemoteSkillClient(Exception exception)
        {
            _exception = exception;
        }

        public int FetchCount => Volatile.Read(ref _fetchCount);

        public string NormalizeUrl(string? remoteUrl) =>
            remoteUrl ?? throw new InvalidOperationException();

        public async Task<RemoteSkillDefinition> FetchAsync(
            string remoteUrl,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _fetchCount);
            if (_delay > TimeSpan.Zero)
            {
                await Task.Delay(_delay, cancellationToken);
            }

            if (_exception != null)
            {
                throw _exception;
            }

            return _definition!;
        }
    }

    private sealed class TestRefreshLock : IRemoteSkillRefreshLock
    {
        private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

        public async Task<IAsyncDisposable> AcquireAsync(
            Guid skillId,
            CancellationToken cancellationToken)
        {
            var semaphore = _locks.GetOrAdd(skillId, static _ => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync(cancellationToken);
            return new TestLease(semaphore);
        }

        private sealed class TestLease : IAsyncDisposable
        {
            private SemaphoreSlim? _semaphore;

            public TestLease(SemaphoreSlim semaphore)
            {
                _semaphore = semaphore;
            }

            public ValueTask DisposeAsync()
            {
                Interlocked.Exchange(ref _semaphore, null)?.Release();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly string _root;

        private TestDatabase(string root, string connectionString)
        {
            _root = root;
            ConnectionString = connectionString;
        }

        private string ConnectionString { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"agw-remote-skill-cache-{Guid.CreateVersion7():N}");
            Directory.CreateDirectory(root);
            var database = new TestDatabase(
                root,
                $"Data Source={Path.Combine(root, "cache.db")};Default Timeout=5");
            await using var node = database.CreateNode();
            await using var scope = node.CreateAsyncScope();
            await scope.ServiceProvider
                .GetRequiredService<AgwDbContext>()
                .Database
                .EnsureCreatedAsync(TestContext.Current.CancellationToken);
            return database;
        }

        public ServiceProvider CreateNode()
        {
            var services = new ServiceCollection();
            services.AddDbContext<AgwDbContext>(
                options => options.UseSqlite(ConnectionString));
            services.AddScoped<DbContext>(
                provider => provider.GetRequiredService<AgwDbContext>());
            services.AddScoped(typeof(IRepository<>), typeof(EfRepository<>));
            services.AddScoped<IUnitOfWork>(
                provider => provider.GetRequiredService<AgwDbContext>());
            return services.BuildServiceProvider();
        }

        public async Task SeedAsync(
            Skill skill,
            RemoteSkillCache? cache = null)
        {
            await using var node = CreateNode();
            await using var scope = node.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
            context.Skills.Add(skill);
            if (cache != null)
            {
                context.RemoteSkillCaches.Add(cache);
            }

            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(_root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
