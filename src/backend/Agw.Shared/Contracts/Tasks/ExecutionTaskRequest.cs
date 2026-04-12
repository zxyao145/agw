using Agw.Shared.Data.Entities.Tasks;

using Microsoft.AspNetCore.Mvc;

namespace Agw.Shared.Contracts.Tasks;

public sealed record ExecutionTaskRequest(
    Guid? TaskId,
    Guid? ProjectId,
    string Input,
    bool Resume,
    string User);


public readonly record struct ExecutionTaskResolutionResult(ProjectTask? Task, IActionResult? Error);
