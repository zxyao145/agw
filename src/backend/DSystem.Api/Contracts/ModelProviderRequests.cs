using System;

namespace DSystem.Api.Contracts;

public record ModelProviderCreateRequest(
    Guid ModelId,
    Guid ProviderId,
    decimal InputPrice,
    decimal OutputPrice,
    decimal CacheRead,
    decimal CacheWrite,
    int RpsLimit);

public record ModelProviderUpdateRequest(
    decimal InputPrice,
    decimal OutputPrice,
    decimal CacheRead,
    decimal CacheWrite,
    int RpsLimit);
