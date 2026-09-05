using System.Linq.Expressions;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Agw.Skills.Application;
using Agw.Skills.Application.Remote;
using Agw.Skills.Contracts.Registration;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Skills.Tests;

public class SkillAppServiceBuiltInTests
{
    private static readonly Guid BuiltInSkillId = Guid.Parse("11111111-1111-1111-8888-000000000002");

    [Fact]
    public async Task BuiltInSkill_IsReportedAndCannotBeUpdatedOrDeleted()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agw-built-in-skill-{Guid.CreateVersion7():N}");
        var dataPaths = AgwDataPaths.Resolve(root, "/unused");
        dataPaths.EnsureCreated();
        try
        {
            var skill = new Skill
            {
                Id = BuiltInSkillId,
                Name = "agw-job",
                Description = "Manage jobs.",
                Kind = SkillKind.BuiltIn,
                ContentPath = string.Empty,
            };
            var unitOfWork = new TestUnitOfWork();
            var service = new SkillAppService(
                new TestRepository<Skill>([skill], entity => entity.Id),
                new TestAgentReferenceFacade(new TestRepository<AgentSkillRelation>([], _ => Guid.Empty), unitOfWork),
                new TestRepository<RemoteSkillCache>([], entity => entity.SkillId),
                unitOfWork,
                dataPaths,
                NullLogger<SkillAppService>.Instance,
                new TestRemoteSkillClient(),
                new TestRemoteSkillRefreshLock(),
                TimeProvider.System,
                new TestCurrentUser("test-user"),
                [new TestSkillRegistration()]
            );

            var details = Assert.Single(await service.ListAsync());
            Assert.Equal(BuiltInSkillId, details.Skill.Id);
            Assert.True(details.IsBuiltIn);

            var updateException = await Assert.ThrowsAsync<AgwException>(() =>
                service.UpdateAsync(
                    BuiltInSkillId,
                    "renamed",
                    "Changed",
                    archive: null,
                    "test-user",
                    cancellationToken: TestContext.Current.CancellationToken
                )
            );
            Assert.Equal(ErrorCodes.BuiltInSkillImmutable.Code, updateException.Code);

            var deleteException = await Assert.ThrowsAsync<AgwException>(() =>
                service.DeleteAsync(BuiltInSkillId, TestContext.Current.CancellationToken)
            );
            Assert.Equal(ErrorCodes.BuiltInSkillImmutable.Code, deleteException.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class TestSkillRegistration : IAgentSkillRegistration
    {
        public Guid Id => BuiltInSkillId;

        public string Name => "agw-job";

        public string Description => "Manage jobs.";

        public AgentSkill Create(Guid projectId) => throw new NotSupportedException();
    }

    private sealed class TestRemoteSkillClient : IRemoteSkillClient
    {
        public string NormalizeUrl(string? remoteUrl) => throw new NotSupportedException();

        public Task<RemoteSkillDefinition> FetchAsync(
            string remoteUrl,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }

    private sealed class TestRemoteSkillRefreshLock : IRemoteSkillRefreshLock
    {
        public Task<IAsyncDisposable> AcquireAsync(Guid skillId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TestUnitOfWork : IUnitOfWork
    {
        public void Dispose() { }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task<bool> SaveEntitiesAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class TestRepository<TEntity> : IRepository<TEntity>
        where TEntity : class
    {
        private readonly List<TEntity> _items;
        private readonly Func<TEntity, Guid> _idSelector;

        public TestRepository(IEnumerable<TEntity> items, Func<TEntity, Guid> idSelector)
        {
            _items = items.ToList();
            _idSelector = idSelector;
        }

        public IQueryable<TEntity> Queryable => _items.AsQueryable();

        public Task<TEntity?> GetByIdAsync(object id)
        {
            var typedId = Assert.IsType<Guid>(id);
            return Task.FromResult(_items.SingleOrDefault(item => _idSelector(item) == typedId));
        }

        public Task<TEntity?> SingleOrDefaultAsync(
            Expression<Func<TEntity, bool>> predicate,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(_items.AsQueryable().SingleOrDefault(predicate));

        public Task<IReadOnlyList<TEntity>> ListAsync(
            Expression<Func<TEntity, bool>>? predicate = null,
            Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null
        )
        {
            IQueryable<TEntity> query = _items.AsQueryable();
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
            params Expression<Func<TEntity, object>>[] includes
        ) => ListAsync(predicate, orderBy);

        public Task AddAsync(TEntity entity)
        {
            _items.Add(entity);
            return Task.CompletedTask;
        }

        public void Update(TEntity entity) { }

        public void Remove(TEntity entity)
        {
            _items.Remove(entity);
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
