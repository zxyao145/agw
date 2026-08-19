using Agw.Infrastructure.Data;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Agw.Integrations.Tests;

public class IntegrationTableCommentTests
{
    [Fact]
    public void Model_IntegrationTables_HaveExpectedComments()
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>().UseSqlite("Data Source=:memory:").Options;
        using var dbContext = new AgwDbContext(options);
        var expectedComments = new Dictionary<Type, string>
        {
            [typeof(PluginInstallation)] = "Stores platform-wide plugin installation configuration.",
            [typeof(PluginInstallationCredential)] = "Stores protected credentials owned by a plugin installation.",
            [typeof(Connection)] = "Represents an external account or service endpoint available to agents.",
            [typeof(ConnectionCredential)] = "Stores protected credentials owned by an integration connection.",
            [typeof(AgentConnectionRelation)] = "Binds an agent to an integration connection.",
            [typeof(ProjectConnectionRelation)] = "Binds a project to an integration connection.",
        };
        var model = dbContext.GetService<IDesignTimeModel>().Model;

        foreach (var (entityType, expectedComment) in expectedComments)
        {
            var metadata = model.FindEntityType(entityType);

            Assert.NotNull(metadata);
            Assert.Equal(expectedComment, metadata.GetComment());
        }
    }
}
