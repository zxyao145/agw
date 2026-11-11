using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agw.Extensions.Sdk.EntityFrameworkCore;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ExtensionCancelScheduleRequest))]
[JsonSerializable(typeof(ExtensionDeleteManagedSubscriptionRequest))]
internal partial class ExtensionOutboxPayloadJsonContext : JsonSerializerContext;
