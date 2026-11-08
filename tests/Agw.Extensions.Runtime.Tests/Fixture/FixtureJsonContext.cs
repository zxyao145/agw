using System.Text.Json.Serialization;

namespace Agw.Extensions.Runtime.Tests.Fixture;

[JsonSerializable(typeof(EchoResponse))]
[JsonSerializable(typeof(DatabaseProbeResponse))]
[JsonSerializable(typeof(DatabaseInvocationResponse))]
[JsonSerializable(typeof(DatabaseModelResponse))]
[JsonSerializable(typeof(DatabaseProbeState))]
[JsonSerializable(typeof(DatabaseProbeState[]))]
[JsonSerializable(typeof(DatabaseProbeNoteState))]
[JsonSerializable(typeof(DatabaseProbeOwnershipResponse))]
[JsonSerializable(typeof(CapabilityProbeResponse))]
internal partial class FixtureJsonContext : JsonSerializerContext { }

internal sealed record EchoResponse(string Value);

internal sealed record DatabaseProbeResponse(int Count);

internal sealed record CapabilityProbeResponse(Guid? ProjectId, string? UserId);
