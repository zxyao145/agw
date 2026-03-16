namespace DSystem.Manager.Api.Contracts;

public record ApiKeyCreateRequest(Guid ModelProviderId, string ApiKey, bool Enable = true);

public record ApiKeyUpdateRequest(string ApiKey, bool Enable);
