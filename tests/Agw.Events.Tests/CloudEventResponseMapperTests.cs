using System.Text.Json;
using Agw.Events.Api.Dtos;
using Agw.Events.Application;
using Agw.Shared.Contracts.Events;
using Xunit;

namespace Agw.Events.Tests;

public sealed class CloudEventResponseMapperTests
{
    [Fact]
    public void From_JsonData_PreservesJsonValueAndTagsArray()
    {
        using var json = JsonDocument.Parse("""{"state":"open"}""");
        var cloudEvent = new EventCloudEvent
        {
            Id = "event-id",
            Source = "/issues",
            Type = "com.example.issue",
            DataContentType = "application/json",
            Data = new CloudEventData
            {
                Kind = CloudEventDataKind.Json,
                Text = json.RootElement.GetRawText(),
                Json = json.RootElement.Clone(),
            },
            Scope = "team-a",
            Tags = ["state:open", "priority:high"],
        };

        var response = CloudEventResponseMapper.From(cloudEvent);
        var serialized = JsonSerializer.SerializeToElement(response);

        Assert.Equal("1.0", serialized.GetProperty("specversion").GetString());
        Assert.Equal(JsonValueKind.Object, serialized.GetProperty("data").ValueKind);
        Assert.Equal("open", serialized.GetProperty("data").GetProperty("state").GetString());
        Assert.Equal("team-a", serialized.GetProperty("scope").GetString());
        Assert.Equal(2, serialized.GetProperty("tags").GetArrayLength());
        Assert.False(serialized.TryGetProperty("data_base64", out _));
    }

    [Fact]
    public void From_TextData_SerializesDataAsString()
    {
        var response = CloudEventResponseMapper.From(
            new EventCloudEvent
            {
                Id = "event-id",
                Source = "/issues",
                Type = "com.example.issue",
                DataContentType = "text/plain",
                Data = new CloudEventData { Kind = CloudEventDataKind.Text, Text = "plain text" },
            }
        );

        Assert.Equal(JsonValueKind.String, response.Data?.ValueKind);
        Assert.Equal("plain text", response.Data?.GetString());
        Assert.Null(response.DataBase64);
    }

    [Fact]
    public void From_BinaryData_UsesDataBase64()
    {
        var response = CloudEventResponseMapper.From(
            new EventCloudEvent
            {
                Id = "event-id",
                Source = "/issues",
                Type = "com.example.issue",
                DataContentType = "application/octet-stream",
                Data = new CloudEventData { Kind = CloudEventDataKind.Binary, Binary = [1, 2] },
            }
        );
        var serialized = JsonSerializer.SerializeToElement(response);

        Assert.Null(response.Data);
        Assert.Equal("AQI=", serialized.GetProperty("data_base64").GetString());
        Assert.False(serialized.TryGetProperty("data", out _));
    }
}
