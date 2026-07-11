using Agw.Providers.Contracts.Manager;
using Agw.Shared.Data.Entities.Providers;

namespace Agw.Tasks.Tests;

public class ModelContractTests
{
    [Theory]
    [InlineData(typeof(LlmModel))]
    [InlineData(typeof(ModelCreateRequest))]
    [InlineData(typeof(ModelUpdateRequest))]
    public void PublicContract_DoesNotExposeType(Type contractType)
    {
        Assert.Null(contractType.GetProperty("Type"));
    }
}
