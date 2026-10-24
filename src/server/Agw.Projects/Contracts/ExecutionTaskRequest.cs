using Microsoft.AspNetCore.Mvc;

namespace Agw.Projects.Application;

/// <summary>
/// 解析执行任务的请求；RequesterExtensionId 是请求方 extension 的 id，宿主为 null。
/// A request to resolve an execution task; RequesterExtensionId is the requesting extension id, null for the host.
/// </summary>
public sealed record ExecutionTaskRequest(
    Guid? TaskId,
    Guid ConversationId,
    Guid? ProjectId,
    string? ContextId,
    string Input,
    bool Resume,
    string User,
    string? RequesterExtensionId
);

public readonly record struct ExecutionTaskResolutionResult(TaskProjection? Task, IActionResult? Error);
