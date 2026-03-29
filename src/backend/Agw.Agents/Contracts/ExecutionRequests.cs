using Agw.Shared.Enums;
using Agw.Shared.Models;
using System.Text.Json.Serialization;

namespace Agw.Api.Contracts;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SettingRequest), nameof(SettingRequest))]
[JsonDerivedType(typeof(ExecRequest), nameof(ExecRequest))]
[JsonDerivedType(typeof(InterruptRequest), nameof(InterruptRequest))]
public abstract record AgentRunCommand;

public record SettingRequest(string SettingContent) : AgentRunCommand;

public record ExecRequest(
    AgentRuntimeType AgentType,
    AgwUserInput Input,
    string? SessionId = null,
    Guid? ProjectId = null,
    Guid? TaskId = null) : AgentRunCommand;

public record InterruptRequest(string? Reason = null) : AgentRunCommand;
