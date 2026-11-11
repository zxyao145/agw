using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agw.Extensions.Feishu;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(FeishuNodeApplication))]
[JsonSerializable(typeof(FeishuNodeChat[]))]
[JsonSerializable(typeof(FeishuNodeEvent))]
[JsonSerializable(typeof(FeishuNodeMessage))]
[JsonSerializable(typeof(FeishuNodeVerification))]
[JsonSerializable(typeof(FeishuNodeWebhookRequest))]
[JsonSerializable(typeof(FeishuNodeWorkerResponse))]
[JsonSerializable(typeof(JsonElement))]
internal partial class FeishuNodeJsonContext : JsonSerializerContext;

internal sealed record FeishuNodeWorkerResponse
{
    public required bool Ok { get; init; }

    public JsonElement Payload { get; init; }
}
