using Agw.Shared.Contracts.Events;
using Xunit;

namespace Agw.Events.Tests;

public sealed class CloudEventDataCodecTests
{
    [Fact]
    public void StoreData_UsesTextAndBase64Representations()
    {
        Assert.Null(CloudEventDataCodec.StoreData(null));
        Assert.Equal(
            """{"state":"open"}""",
            CloudEventDataCodec.StoreData(
                new CloudEventData { Kind = CloudEventDataKind.Json, Text = """{"state":"open"}""" }
            )
        );
        Assert.Equal(
            "hello",
            CloudEventDataCodec.StoreData(new CloudEventData { Kind = CloudEventDataKind.Text, Text = "hello" })
        );
        Assert.Equal(
            "AQI=",
            CloudEventDataCodec.StoreData(new CloudEventData { Kind = CloudEventDataKind.Binary, Binary = [1, 2] })
        );
    }

    [Fact]
    public void ReadData_UsesMediaTypeAndBinaryFlag()
    {
        var json = CloudEventDataCodec.ReadData("""{"state":"open"}""", null, false);
        var text = CloudEventDataCodec.ReadData("hello", "text/plain", false);
        var binary = CloudEventDataCodec.ReadData("AQI=", "application/octet-stream", true);

        Assert.Equal(CloudEventDataKind.Json, json?.Kind);
        Assert.Equal("open", json?.Json?.GetProperty("state").GetString());
        Assert.Equal(CloudEventDataKind.Text, text?.Kind);
        Assert.Equal("hello", text?.Text);
        Assert.Equal(CloudEventDataKind.Binary, binary?.Kind);
        Assert.Equal(new byte[] { 1, 2 }, binary?.Binary);
    }

    [Fact]
    public void IsExplicitJsonContentType_RequiresJsonMediaType()
    {
        Assert.True(CloudEventDataCodec.IsJsonContentType(null));
        Assert.False(CloudEventDataCodec.IsExplicitJsonContentType(null));
        Assert.True(CloudEventDataCodec.IsExplicitJsonContentType("application/problem+json; charset=utf-8"));
        Assert.False(CloudEventDataCodec.IsExplicitJsonContentType("text/plain"));
    }
}
