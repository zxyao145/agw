using System.Text.Json;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Exceptions;
using Xunit;

namespace Agw.Events.Tests;

public sealed class CloudEventJsonLogicContextTests
{
    [Fact]
    public void Create_Envelope_UsesTypedDataScopeAndTagArray()
    {
        using var data = JsonDocument.Parse("""{"state":"open"}""");
        var cloudEvent = new CloudEventEnvelope
        {
            Id = "event-id",
            Source = "/issues",
            Type = "com.example.issue",
            DataContentType = "application/json",
            Data = new CloudEventData
            {
                Kind = CloudEventDataKind.Json,
                Text = data.RootElement.GetRawText(),
                Json = data.RootElement.Clone(),
            },
            Extensions = new Dictionary<string, CloudEventAttributeValue>
            {
                ["scope"] = new(CloudEventAttributeKind.String, "team-a"),
                ["tags"] = new(CloudEventAttributeKind.String, "state:open,priority:high"),
            },
        };

        var context = CloudEventJsonLogicContext.Create(cloudEvent);

        Assert.Equal("team-a", context["scope"]?.GetValue<string>());
        Assert.Equal("[\"state:open\",\"priority:high\"]", context["tags"]?.ToJsonString());
        Assert.Equal("open", context["data"]?["state"]?.GetValue<string>());
    }

    [Fact]
    public void Create_Envelope_BinaryDataIsNull()
    {
        var cloudEvent = new CloudEventEnvelope
        {
            Id = "event-id",
            Source = "/issues",
            Type = "com.example.issue",
            Data = new CloudEventData { Kind = CloudEventDataKind.Binary, Binary = [1, 2, 3] },
        };

        var context = CloudEventJsonLogicContext.Create(cloudEvent);

        Assert.Null(context["data"]);
    }

    [Fact]
    public void Create_EnvelopeWithInvalidTags_RejectsTags()
    {
        var cloudEvent = new CloudEventEnvelope
        {
            Id = "event-id",
            Source = "/issues",
            Type = "com.example.issue",
            Extensions = new Dictionary<string, CloudEventAttributeValue>
            {
                ["tags"] = new(CloudEventAttributeKind.String, "state:open,,priority:high"),
            },
        };

        var exception = Assert.Throws<AgwException>(() => CloudEventJsonLogicContext.Create(cloudEvent));

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }
}
