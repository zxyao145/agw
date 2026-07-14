using Agw.Infrastructure.Repositories;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Tests;

public class EfRepositoryTests
{
    [Fact]
    public async Task ListAsync_WhenOnlyOrderByProvided_ReturnsEntitiesInSpecifiedOrder()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = new DbContextOptionsBuilder<RepositoryTestDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var setupContext = new RepositoryTestDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
        }

        await using (var seedContext = new RepositoryTestDbContext(options))
        {
            seedContext.SortableEntities.AddRange(
                new SortableEntity { Id = 1, Category = "keep", Rank = 2 },
                new SortableEntity { Id = 2, Category = "drop", Rank = 0 },
                new SortableEntity { Id = 3, Category = "keep", Rank = 3 },
                new SortableEntity { Id = 4, Category = "keep", Rank = 1 });

            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new RepositoryTestDbContext(options);
        var repository = new EfRepository<SortableEntity>(dbContext);

        var results = await repository.ListAsync(
            null,
            (IQueryable<SortableEntity> query) => query.OrderBy(entity => entity.Rank)
            );

        Assert.Collection(
            results,
            entity => Assert.Equal(0, entity.Rank),
            entity => Assert.Equal(1, entity.Rank),
            entity => Assert.Equal(2, entity.Rank),
            entity => Assert.Equal(3, entity.Rank));
    }

    [Fact]
    public async Task ListAsync_WhenOrderByProvided_ReturnsFilteredEntitiesInSpecifiedOrder()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = new DbContextOptionsBuilder<RepositoryTestDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var setupContext = new RepositoryTestDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
        }

        await using (var seedContext = new RepositoryTestDbContext(options))
        {
            seedContext.SortableEntities.AddRange(
                new SortableEntity { Id = 1, Category = "keep", Rank = 2 },
                new SortableEntity { Id = 2, Category = "drop", Rank = 0 },
                new SortableEntity { Id = 3, Category = "keep", Rank = 3 },
                new SortableEntity { Id = 4, Category = "keep", Rank = 1 });

            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new RepositoryTestDbContext(options);
        var repository = new EfRepository<SortableEntity>(dbContext);

        var results = await repository.ListAsync(
            entity => entity.Category == "keep",
            (IQueryable<SortableEntity> query) => query.OrderBy(entity => entity.Rank));

        Assert.Collection(
            results,
            entity => Assert.Equal(1, entity.Rank),
            entity => Assert.Equal(2, entity.Rank),
            entity => Assert.Equal(3, entity.Rank));
    }

    [Fact]
    public async Task ListAsync_WithIncludesAndOrderBy_LoadsNavigationAndAppliesSortOrder()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = new DbContextOptionsBuilder<RepositoryTestDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var setupContext = new RepositoryTestDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
        }

        await using (var seedContext = new RepositoryTestDbContext(options))
        {
            seedContext.RelatedEntities.AddRange(
                new RelatedEntity { Id = 1, Name = "first" },
                new RelatedEntity { Id = 2, Name = "second" },
                new RelatedEntity { Id = 3, Name = "third" });

            seedContext.SortableEntities.AddRange(
                new SortableEntity { Id = 1, Category = "keep", Rank = 2, RelatedEntityId = 1 },
                new SortableEntity { Id = 2, Category = "drop", Rank = 0, RelatedEntityId = 2 },
                new SortableEntity { Id = 3, Category = "keep", Rank = 3, RelatedEntityId = 3 },
                new SortableEntity { Id = 4, Category = "keep", Rank = 1, RelatedEntityId = 2 });

            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new RepositoryTestDbContext(options);
        var repository = new EfRepository<SortableEntity>(dbContext);

        var results = await repository.ListAsync(
            entity => entity.Category == "keep",
            (IQueryable<SortableEntity> query) => query.OrderByDescending(entity => entity.Rank),
            entity => entity.Related!);

        Assert.Collection(
            results,
            entity =>
            {
                Assert.Equal(3, entity.Rank);
                Assert.Equal("third", entity.Related?.Name);
            },
            entity =>
            {
                Assert.Equal(2, entity.Rank);
                Assert.Equal("first", entity.Related?.Name);
            },
            entity =>
            {
                Assert.Equal(1, entity.Rank);
                Assert.Equal("second", entity.Related?.Name);
            });
    }

    private sealed class RepositoryTestDbContext(DbContextOptions<RepositoryTestDbContext> options) : DbContext(options)
    {
        public DbSet<SortableEntity> SortableEntities => Set<SortableEntity>();

        public DbSet<RelatedEntity> RelatedEntities => Set<RelatedEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SortableEntity>()
                .HasOne(entity => entity.Related)
                .WithMany()
                .HasForeignKey(entity => entity.RelatedEntityId);
        }
    }

    private sealed class SortableEntity
    {
        public int Id { get; set; }

        public string Category { get; set; } = string.Empty;

        public int Rank { get; set; }

        public int? RelatedEntityId { get; set; }

        public RelatedEntity? Related { get; set; }
    }

    private sealed class RelatedEntity
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }
}
