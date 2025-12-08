namespace DSystem.Manager.Api.Contracts;

public record ApiKeyCreateRequest(Guid ModelId, Guid ProviderId, string ApiKey, bool Enable = true);

public record ApiKeyUpdateRequest(string ApiKey, bool Enable);
