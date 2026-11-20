using System.Reflection;
using Xunit;

namespace Agw.Extensions.Database.Tests;

public sealed class ExtensionDatabaseSdkContractTests
{
    [Fact]
    public void DatabaseSdk_ExposesContextIdentityOwnershipAndProviderContracts()
    {
        var sdk = Assembly.Load("Agw.Extensions.Sdk");
        var entityFrameworkSdk = Assembly.Load("Agw.Extensions.Sdk.EntityFrameworkCore");
        var contracts = Assembly.Load("Agw.Extensions.Contracts");

        Assert.NotNull(sdk.GetType("Agw.Extensions.Sdk.Database.IExtensionDatabaseIdentity"));
        Assert.NotNull(sdk.GetType("Agw.Extensions.Sdk.Database.IExtensionUserContext"));
        Assert.NotNull(sdk.GetType("Agw.Extensions.Sdk.Database.IExtensionOwnedEntity"));
        Assert.NotNull(sdk.GetType("Agw.Extensions.Sdk.Database.IExtensionEpochValidator"));
        Assert.NotNull(sdk.GetType("Agw.Extensions.Sdk.Database.ExtensionDatabaseProvider"));
        Assert.NotNull(sdk.GetType("Agw.Extensions.Sdk.Database.IExtensionDatabaseConnection"));
        Assert.NotNull(sdk.GetType("Agw.Extensions.Sdk.Database.IExtensionDatabaseFactory"));
        Assert.NotNull(sdk.GetType("Agw.Extensions.Sdk.Database.IExtensionDatabaseGeneration"));
        Assert.NotNull(entityFrameworkSdk.GetType("Agw.Extensions.Sdk.Database.ExtensionDbContext"));
        Assert.NotNull(contracts.GetType("Agw.Extensions.Contracts.IExtensionDatabase"));
    }
}
