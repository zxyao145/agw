using Agw.Setup.Services;

using Xunit;

namespace Agw.Setup.Tests;

public sealed class SetupCodeServiceTests
{
    [Fact]
    public void Consume_WhenCodeIsUsedTwice_OnlyFirstAttemptSucceeds()
    {
        var service = new SetupCodeService("ABCD-EFGH-IJKL");

        Assert.True(service.Matches("ABCD-EFGH-IJKL"));
        Assert.True(service.Consume("ABCD-EFGH-IJKL"));
        Assert.False(service.Matches("ABCD-EFGH-IJKL"));
        Assert.False(service.Consume("ABCD-EFGH-IJKL"));
    }
}
