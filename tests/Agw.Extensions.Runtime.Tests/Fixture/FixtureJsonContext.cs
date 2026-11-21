using System.Text.Json.Serialization;
using Agw.Events.Contracts;
using Agw.Shared.Contracts.Events;

namespace Agw.Extensions.Runtime.Tests.Fixture;

[JsonSerializable(typeof(EchoResponse))]
[JsonSerializable(typeof(OutboxEnqueueResponse))]
[JsonSerializable(typeof(OwnedRecordResponse))]
[JsonSerializable(typeof(CloudEventDraft))]
[JsonSerializable(typeof(EventPublishResult))]
[JsonSerializable(typeof(DatabaseProbeResponse))]
[JsonSerializable(typeof(DatabaseInvocationResponse))]
[JsonSerializable(typeof(DatabaseModelResponse))]
[JsonSerializable(typeof(DatabaseProbeState))]
[JsonSerializable(typeof(DatabaseProbeState[]))]
[JsonSerializable(typeof(DatabaseProbeNoteState))]
[JsonSerializable(typeof(DatabaseProbeOwnershipResponse))]
[JsonSerializable(typeof(CapabilityProbeResponse))]
[JsonSerializable(typeof(SdkCapabilityResponse))]
internal partial class FixtureJsonContext : JsonSerializerContext { }

internal sealed record EchoResponse(string Value);

internal sealed record OutboxEnqueueResponse(Guid Id);

internal sealed record DatabaseProbeResponse(int Count);

internal sealed record CapabilityProbeResponse(Guid? ProjectId, string? UserId);

internal sealed record SdkCapabilityResponse(bool LeaseLost, bool SecretRoundTrips);
