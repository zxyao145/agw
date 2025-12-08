using System;
using DSystem.Domain.Enums;

namespace DSystem.Api.Contracts;

public record ModelCreateRequest(string Name, string? Description, ModelType Type, int MaxTokens);

public record ModelUpdateRequest(string Name, string? Description, ModelType Type, int MaxTokens);
