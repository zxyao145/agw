using System.Text.Json;
using System.Text.Json.Serialization;
using Agw.Notifications.Contracts;

namespace Agw.Extensions.Feishu;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(FeishuAppIdRequest))]
[JsonSerializable(typeof(FeishuAppResponse))]
[JsonSerializable(typeof(FeishuAppResponse[]))]
[JsonSerializable(typeof(FeishuAppVersionRequest))]
[JsonSerializable(typeof(FeishuAppWriteRequest))]
[JsonSerializable(typeof(FeishuBindConfirmRequest))]
[JsonSerializable(typeof(FeishuBindDeleteRequest))]
[JsonSerializable(typeof(FeishuBindStartResponse))]
[JsonSerializable(typeof(FeishuBindStartRequest))]
[JsonSerializable(typeof(FeishuCallbackAcknowledgement))]
[JsonSerializable(typeof(FeishuChatResponse[]))]
[JsonSerializable(typeof(FeishuIdentityBindingResponse))]
[JsonSerializable(typeof(FeishuIdentityBindingResponse[]))]
[JsonSerializable(typeof(FeishuNodeMessage))]
[JsonSerializable(typeof(FeishuTargetResponse))]
[JsonSerializable(typeof(FeishuTargetResponse[]))]
[JsonSerializable(typeof(FeishuTargetDeleteRequest))]
[JsonSerializable(typeof(FeishuTargetSaveRequest))]
[JsonSerializable(typeof(FeishuTestSendRequest))]
[JsonSerializable(typeof(FeishuVerificationResponse))]
[JsonSerializable(typeof(NotificationAnswer))]
[JsonSerializable(typeof(NotificationAnswerResult))]
internal partial class FeishuApiJsonContext : JsonSerializerContext;
