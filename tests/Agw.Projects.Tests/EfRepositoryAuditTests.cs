using Agw.Infrastructure.Data;
using Agw.Infrastructure.Data.Interceptors;
using Agw.Infrastructure.Repositories;
using Agw.Shared.Data.Entities.Providers;
using Agw.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Tests;

public class EfRepositoryAuditTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_ExplicitAuditOverrides_ArePreserved(bool detached)
    {
        // Arrange
        _ = new TestUserInfoService();
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 9, 5, 1, 0, 0, TimeSpan.Zero);
        var explicitTime = now.AddDays(-1);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(new EntityModifierInterceptor(new TestAuditUserIdProvider(), new TestTimeProvider(now)))
            .Options;
        await using var context = new AgwDbContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var model = new AgwAiModel
        {
            Name = "model",
            CreateBy = "tester",
            CreateTime = explicitTime.AddDays(-1),
        };
        context.Models.Add(model);
        await context.SaveChangesAsync(cancellationToken);
        if (detached)
            context.ChangeTracker.Clear();
        model.Description = "updated";
        model.UpdateBy = "explicit-actor";
        model.UpdateTime = explicitTime;

        // Act
        new EfRepository<AgwAiModel>(context).Update(model);
        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();

        // Assert
        var persisted = await context.Models.SingleAsync(cancellationToken);
        Assert.Equal("updated", persisted.Description);
        Assert.Equal("tester", persisted.CreateBy);
        Assert.Equal("explicit-actor", persisted.UpdateBy);
        Assert.Equal(explicitTime, persisted.UpdateTime);
    }
}
