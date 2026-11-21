using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Agw.Extensions.Feishu.Tests;

public sealed class FeishuNodeBridgeTests
{
    [Fact]
    public void SerializeCommand_WebhookRequestUsesNodeWorkerWireShape()
    {
        JsonElement message = JsonSerializer.SerializeToElement(
            new { msg_type = "interactive", card = new { elements = Array.Empty<object>() } }
        );
        var request = new FeishuNodeWebhookRequest
        {
            WebhookUrl = "https://open.feishu.cn/open-apis/bot/v2/hook/test",
            WebhookSecret = "signature-secret",
            Message = message,
            DispatchKey = "dispatch-1",
        };
        JsonObject payload = JsonSerializer
            .SerializeToNode(request, FeishuNodeJsonContext.Default.FeishuNodeWebhookRequest)!
            .AsObject();
        string command = FeishuNodeBridge.SerializeCommand("webhook", payload);
        using JsonDocument document = JsonDocument.Parse(command);
        JsonElement root = document.RootElement;

        Assert.Equal("webhook", root.GetProperty("operation").GetString());
        JsonElement serializedPayload = root.GetProperty("payload");
        Assert.Equal(request.WebhookUrl, serializedPayload.GetProperty("webhookUrl").GetString());
        Assert.Equal(request.WebhookSecret, serializedPayload.GetProperty("webhookSecret").GetString());
        Assert.Equal(request.DispatchKey, serializedPayload.GetProperty("dispatchKey").GetString());
        Assert.Equal("interactive", serializedPayload.GetProperty("message").GetProperty("msg_type").GetString());
        Assert.True(serializedPayload.GetProperty("message").TryGetProperty("card", out _));
    }
}
