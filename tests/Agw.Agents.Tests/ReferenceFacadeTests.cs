using Agw.Infrastructure.Data;
using Agw.Integrations.Application.Facades;
using Agw.Providers.Application.Facades;
using Agw.Providers.Contracts.References;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Entities.Skills;
using Agw.Skills.Application.Facades;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Tests;

public sealed class ReferenceFacadeTests
{
    [Fact]
    public void ProviderAuthConfigSnapshot_ToString_RedactsApiKey()
    {
        // Arrange
        var snapshot = new ProviderAuthConfigSnapshot(enable: true, apiKey: "secret-api-key");

        // Act
        var text = snapshot.ToString();

        // Assert
        Assert.DoesNotContain("secret-api-key", text, StringComparison.Ordinal);
        Assert.Contains("***", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelProviderReferenceFacade_OwnerAndForeignIds_ReturnsOnlyOwnedSnapshot()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        await using var dbContext = await CreateDbContextAsync(connection, cancellationToken);
        var ownerUserId = "owner";
        var model = new AgwAiModel
        {
            Id = Guid.CreateVersion7(),
            Name = "owner-model",
            CreateBy = ownerUserId,
        };
        var provider = new Provider
        {
            Id = Guid.CreateVersion7(),
            Name = "owner-provider",
            ProviderType = ProviderType.OpenAIChatCompletions,
            Endpoint = "https://api.example.test",
            CreateBy = ownerUserId,
            AuthConfigs =
            [
                new ProviderAuthConfig
                {
                    Id = Guid.CreateVersion7(),
                    ProviderId = Guid.Empty,
                    ApiKey = "owner-key",
                    Enable = true,
                },
            ],
        };
        provider.AuthConfigs.Single().ProviderId = provider.Id;
        var relation = new ModelProviderRelation
        {
            Id = Guid.CreateVersion7(),
            ModelId = model.Id,
            ProviderId = provider.Id,
            CreateBy = ownerUserId,
        };
        var foreignRelation = new ModelProviderRelation
        {
            Id = Guid.CreateVersion7(),
            ModelId = Guid.CreateVersion7(),
            ProviderId = Guid.CreateVersion7(),
            CreateBy = "foreign",
        };
        dbContext.AddRange(model, provider, relation, foreignRelation);
        await dbContext.SaveChangesAsync(cancellationToken);
        var facade = new ModelProviderReferenceFacade(dbContext, new TestUserInfoService(ownerUserId));

        // Act
        var visibleIds = await facade.FilterVisibleModelProviderIdsAsync(
            [relation.Id, foreignRelation.Id],
            cancellationToken
        );
        var snapshot = await facade.GetRuntimeSnapshotAsync(relation.Id, cancellationToken);
        var hiddenSnapshot = await facade.GetRuntimeSnapshotAsync(foreignRelation.Id, cancellationToken);

        // Assert
        Assert.Equal([relation.Id], visibleIds);
        Assert.NotNull(snapshot);
        Assert.Equal(model.Name, snapshot.Model.Name);
        Assert.Equal(provider.Endpoint, snapshot.Provider.Endpoint);
        Assert.Equal("owner-key", Assert.Single(snapshot.Provider.AuthConfigs).ApiKey);
        Assert.Null(hiddenSnapshot);
    }

    [Fact]
    public async Task SkillReferenceFacade_OwnerBuiltInAndForeignIds_ReturnsOnlyVisibleDescriptors()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        await using var dbContext = await CreateDbContextAsync(connection, cancellationToken);
        var ownerUserId = "owner";
        var ownedSkill = new Skill
        {
            Id = Guid.CreateVersion7(),
            Name = "owned",
            Description = "owned skill",
            Kind = SkillKind.Local,
            ContentPath = "skills/owned",
            CreateBy = ownerUserId,
        };
        var builtInSkill = new Skill
        {
            Id = Guid.CreateVersion7(),
            Name = "built-in",
            Description = "built-in skill",
            Kind = SkillKind.BuiltIn,
            ContentPath = "skills/built-in",
            CreateBy = "system",
        };
        var foreignSkill = new Skill
        {
            Id = Guid.CreateVersion7(),
            Name = "foreign",
            Description = "foreign skill",
            Kind = SkillKind.Local,
            ContentPath = "skills/foreign",
            CreateBy = "foreign",
        };
        dbContext.AddRange(ownedSkill, builtInSkill, foreignSkill);
        await dbContext.SaveChangesAsync(cancellationToken);
        var facade = new SkillReferenceFacade(dbContext, new TestUserInfoService(ownerUserId));

        // Act
        var visibleIds = await facade.FilterVisibleSkillIdsAsync(
            [ownedSkill.Id, builtInSkill.Id, foreignSkill.Id],
            cancellationToken
        );
        var descriptors = await facade.DescribeVisibleSkillsAsync(
            [ownedSkill.Id, builtInSkill.Id, foreignSkill.Id],
            cancellationToken
        );

        // Assert
        Assert.Equal(new[] { ownedSkill.Id, builtInSkill.Id }.OrderBy(id => id), visibleIds.OrderBy(id => id));
        Assert.Equal(
            new[] { ownedSkill.Name, builtInSkill.Name }.OrderBy(name => name),
            descriptors.Select(descriptor => descriptor.Name).OrderBy(name => name)
        );
    }

    [Fact]
    public async Task ConnectionReferenceFacade_OwnerAndForeignIds_ReturnsOnlyOwnedIds()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        await using var dbContext = await CreateDbContextAsync(connection, cancellationToken);
        var ownerUserId = "owner";
        var ownedConnection = new Connection
        {
            Id = Guid.CreateVersion7(),
            Alias = "owned",
            CreateBy = ownerUserId,
        };
        var foreignConnection = new Connection
        {
            Id = Guid.CreateVersion7(),
            Alias = "foreign",
            CreateBy = "foreign",
        };
        dbContext.AddRange(ownedConnection, foreignConnection);
        await dbContext.SaveChangesAsync(cancellationToken);
        var facade = new ConnectionReferenceFacade(dbContext, new TestUserInfoService(ownerUserId));

        // Act
        var visibleIds = await facade.FilterOwnedConnectionIdsAsync(
            [ownedConnection.Id, foreignConnection.Id],
            cancellationToken
        );

        // Assert
        Assert.Equal([ownedConnection.Id], visibleIds);
    }

    private static async Task<AgwDbContext> CreateDbContextAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken
    )
    {
        var dbContext = new AgwDbContext(new DbContextOptionsBuilder<AgwDbContext>().UseSqlite(connection).Options);
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        return dbContext;
    }
}
