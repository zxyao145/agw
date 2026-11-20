using System.Text.Json;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;
using Xunit;

namespace Agw.Events.Application.Sources;

public sealed class EventSourceTypeDeclarationTests
{
    [Fact]
    public void Resolve_TimerSource_ReturnsEventType()
    {
        var source = new EventSource
        {
            Kind = EventSourceKind.Timer,
            ConfigJson = JsonSerializer.Serialize(
                new
                {
                    kind = "Once",
                    at = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    timeZone = "UTC",
                    @event = new { type = "demo.daily" },
                }
            ),
        };

        Assert.Equal(["demo.daily"], EventSourceTypeDeclaration.Resolve(source, null));
    }

    [Fact]
    public void Resolve_BuiltInSource_ReturnsDeclaredTypes()
    {
        var source = new EventSource
        {
            Name = EventBuiltInSources.Execution,
            Kind = EventSourceKind.Publisher,
            ManagedBy = EventManagedBy.Agw,
        };

        Assert.Equal(["agw.execution.completed"], EventSourceTypeDeclaration.Resolve(source, null));
    }

    [Fact]
    public void Resolve_HttpPollWithConfiguredShortInterval_ReturnsDeclaredTypes()
    {
        var source = new EventSource
        {
            Kind = EventSourceKind.HttpPoll,
            ConfigJson = JsonSerializer.Serialize(
                new
                {
                    eventTypes = new[] { "demo.*" },
                    request = new { url = "https://example.test/events" },
                    interval = "PT30S",
                }
            ),
        };

        Assert.Equal(["demo.*"], EventSourceTypeDeclaration.Resolve(source, null));
    }
}
