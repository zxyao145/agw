using System.Text.Json.Serialization;

namespace Agw.Extensions.TaskPilot;

[JsonSerializable(typeof(TaskPilotDeliveryRuleConfig))]
[JsonSerializable(typeof(TaskPilotDeliveryProjectConfig))]
[JsonSerializable(typeof(TaskPilotReviewResult))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class TaskPilotDeliveryJsonContext : JsonSerializerContext { }
