using Agw.Shared.Contracts.Events;

namespace Agw.Shared.Tests;

public sealed class EventManagedByTests
{
    [Fact]
    public void ForExtension_ExtensionId_ReturnsPrefixedLowercaseId()
    {
        var extensionId = Guid.Parse("019A4C17-0000-7000-8000-00000000ABCD");

        var managedBy = EventManagedBy.ForExtension(extensionId);

        Assert.Equal("ext:019a4c17-0000-7000-8000-00000000abcd", managedBy);
        Assert.True(EventManagedBy.IsExtension(managedBy));
    }

    [Theory]
    [InlineData("user")]
    [InlineData("job")]
    [InlineData("extension")]
    public void IsExtension_OtherValues_ReturnsFalse(string managedBy) =>
        Assert.False(EventManagedBy.IsExtension(managedBy));
}
