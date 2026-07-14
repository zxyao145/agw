using Agw.Infrastructure.Repositories;
using Agw.Integrations;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Repositories;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Infrastructure.Tests;

public class AppDefinitionRepoTests
{
    [Fact]
    public async Task GetByIdAsync_WhenNameMatchesStaticCatalog_ReturnsDefinition()
    {
        var repository = new AppDefinitionRepo();

        var definition = await repository.GetByIdAsync("github");

        Assert.NotNull(definition);
        Assert.Equal("github", definition.Name);
        Assert.Equal(
            IntegrationConstants.AppList.Single(item => item.Name == "github").DisplayName,
            definition.DisplayName);
    }

    [Fact]
    public async Task AddAsync_WhenCalled_ThrowsNotSupportedException()
    {
        var repository = new AppDefinitionRepo();

        var definition = new AppDefinition
        {
            Name = "custom-app",
            DisplayName = "Custom App",
            Category = CategoryType.Other,
            Provider = "Custom",
            Description = "Test",
            AuthUrl = "https://example.test/oauth",
            TokenEndpoint = "https://example.test/token",
            Scopes = [],
            ToolNames = []
        };

        await Assert.ThrowsAsync<NotSupportedException>(() => repository.AddAsync(definition));
    }

    [Fact]
    public void AddInfrastructure_WhenResolvingAppDefinitionRepository_UsesStaticCatalogRepository()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "sqlite",
                ["Database:ConnectionString"] = "Data Source=:memory:"
            })
            .Build();

        services.AddInfrastructure(configuration);

        using var serviceProvider = services.BuildServiceProvider();
        var repository = serviceProvider.GetRequiredService<IRepository<AppDefinition>>();

        Assert.IsType<AppDefinitionRepo>(repository);
    }
}
