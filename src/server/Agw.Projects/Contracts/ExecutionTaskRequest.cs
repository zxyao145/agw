using Microsoft.AspNetCore.Mvc;

namespace Agw.Projects.Application;

public sealed record ExecutionTaskRequest(
    Guid? TaskId,
    Guid? ProjectId,
    string? ContextId,
    string Input,
    bool Resume,
    string User
);

public readonly record struct ExecutionTaskResolutionResult(TaskProjection? Task, IActionResult? Error);
