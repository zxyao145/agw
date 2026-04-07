# Chat Task Generalization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Convert `ProjectTask` into a pure task/session container, make `/chat` generic for `Agent` and `Agentflow`, and move execution ownership back to `Job` or per-turn chat metadata.

**Architecture:** Backend task persistence stops storing task-level target bindings and instead stores only session identity, status, title, and optional `JobId`. Scheduled execution stays job-driven, while `/chat` routes directly by the selected target and records `targetType` plus `targetId` in each turn's `TaskRecord.Metadata`. Frontend project-task pages become read-only history views, and `/chat` is reduced to a generic project-plus-target workspace with no Claude-specific settings, file sidecars, or task-target assumptions.

**Tech Stack:** ASP.NET Core, EF Core, xUnit, Next.js 16 App Router, React 19, TanStack React Query 5, openapi-typescript, TypeScript, oxlint

---

## File Structure

- `src/backend/Agw.Shared/Contracts/Tasks/Entities/ProjectTask.cs`
  Owns the persistent task/session shape. Remove `AgentType`, `AgentId`, and `Description`; add nullable `JobId`.
- `src/backend/Agw.Shared/Contracts/ProjectTaskRequests.cs`
  Owns task DTOs. Remove task-create/update/reorder target fields, remove description fields, add `JobId` to read models.
- `src/backend/Agw.Shared/Contracts/Tasks/ITaskAppService.cs`
  Owns the execution-time task creation interface. Drop target-binding parameters from `CreateTaskForExecutionAsync`.
- `src/backend/Agw.Infrastructure/Data/LlmDbContext.cs`
  Owns EF model configuration for `ProjectTask` and `TaskRecord`.
- `src/backend/Agw.Tasks/DomainServices/ProjectTaskDomainService.cs`
  Owns task validation and status transitions. Remove manual queue-edit helpers tied to task descriptions and pending reordering.
- `src/backend/Agw.Tasks/DomainServices/ProjectTaskTitleFactory.cs`
  New shared helper to derive task titles from chat input or job prompt.
- `src/backend/Agw.Tasks/DomainServices/TaskRecordMetadataFactory.cs`
  New shared helper to extract per-turn `targetType` and `targetId` from a chat message.
- `src/backend/Agw.Tasks/DomainServices/EfCoreChatHistoryProvider.cs`
  Owns interactive chat task creation and record persistence. Generate titles without `Description` and persist turn metadata.
- `src/backend/Agw.Tasks/Services/ProjectTaskAppService.cs`
  Owns task reads, deletes, title updates, job/chat task creation, and terminal status updates.
- `src/backend/Agw.Tasks/Services/TaskAppService.cs`
  Owns interactive execution task creation. Stop storing execution targets on `ProjectTask`.
- `src/backend/Agw.Tasks/Controllers/ProjectTasksController.cs`
  Owns the public task HTTP surface. Remove manual create, update, reorder, and cancel endpoints.
- `src/backend/Agw.Agents/Execution/AgentExecutionCoordinator.cs`
  Owns chat-task reuse and creation during websocket execution. Stop passing target-binding fields into task creation.
- `src/backend/Agw.Agents/Application/AgentExecSession.cs`
  Owns conversion from `AgwUserInput` to `ChatMessage`. Preserve content-level `AdditionalProperties` so metadata survives into `TaskRecord.Metadata`.
- `src/backend/Agw.Jobs/Services/AgentExecutor.cs`
  Owns `Job`-driven task creation and execution. Write `JobId` snapshots and keep execution routing on `Job.AgentType` and `Job.AgentId`.
- `src/backend/Agw.Jobs/DependencyInjection.cs`
  Owns hosted service registration. Remove `ProjectTaskSchedulerHostedService`.
- `src/backend/Agw.Jobs/HostedService/ProjectTaskSchedulerHostedService.cs`
  Delete; `ProjectTask` is no longer a schedulable execution queue.
- `src/backend/Agw.A2A/TaskStore.cs`
  Owns A2A task persistence into `ProjectTask`. Update snapshot fallback logic for the new task shape.
- `src/backend/Agw.A2A/AgentExecutionBridge.cs`
  Owns A2A streaming session bootstrap. Stop constructing `ProjectTask` with removed fields.
- `src/backend/Agw.Infrastructure/Migrations/20260407180000_ProjectTaskGeneralization.cs`
  Generated migration that drops `agent_type`, `agent_id`, and `description`, then adds nullable `job_id`.
- `src/backend/Agw.Infrastructure/Migrations/20260407180000_ProjectTaskGeneralization.Designer.cs`
  Generated EF migration designer.
- `src/backend/Agw.Infrastructure/Migrations/LlmDbContextModelSnapshot.cs`
  Updated EF snapshot after the task-model change.
- `tests/Agw.Tasks.Tests/ProjectTaskDomainServiceTests.cs`
  Verifies session-only task validation and remaining terminal state transitions.
- `tests/Agw.Tasks.Tests/ProjectTaskAppServiceTests.cs`
  Verifies read-model shape and `JobId` persistence.
- `tests/Agw.Tasks.Tests/TaskAppServiceTests.cs`
  New tests for chat task creation with no task-level target binding.
- `tests/Agw.Tasks.Tests/TaskRecordMetadataFactoryTests.cs`
  New tests for per-turn target metadata extraction and title generation helpers.
- `tests/Agw.A2A.Tests/TaskStoreTests.cs`
  Verifies A2A persistence still round-trips after `ProjectTask` shrinks.
- `src/frontend/web/src/api/task-client.ts`
  Frontend task DTO mapping. Remove target/description assumptions, add `jobId`.
- `src/frontend/web/src/app/(app)/(tasks)/projects/[id]/page.tsx`
  Project task list page. Convert to read-only task history.
- `src/frontend/web/src/app/(app)/(tasks)/projects/[id]/tasks/[taskId]/page.tsx`
  Task detail page. Convert to read-only task history with an optional “continue in chat” path.
- `src/frontend/web/src/app/(app)/(tasks)/projects/[id]/tasks/[taskId]/types.ts`
  Task detail DTO shape.
- `src/frontend/web/src/app/(app)/(tasks)/projects/[id]/components/create-task-dialog.tsx`
  Delete. Manual task creation is removed.
- `src/frontend/web/src/components/task/task-list.tsx`
  Shared history sidebar. Remove its direct dependency on Claude-specific settings UI.
- `src/frontend/web/src/app/(app)/(external-agents)/claude-code/page.tsx`
  Keep the external Claude page compiling after `TaskHistoryList` becomes generic.
- `src/frontend/web/src/app/(app)/(interface)/chat/page.tsx`
  Main generic chat page. Add grouped target selection, drop Claude-only settings and file sidecars, and preserve messages plus `taskId` across target switches.
- `src/frontend/web/src/app/(app)/(interface)/chat/types.ts`
  Chat-local types. Replace Claude-specific init/settings types with generic target-state types.
- `src/frontend/web/src/app/(app)/(interface)/chat/components/user-input/input-area.tsx`
  Generic input area with send, stop, clear, and scroll actions only.
- `src/frontend/web/src/app/(app)/(interface)/chat/lib/ai-message-handlers.ts`
  Simplified system-message handling without Claude init parsing.
- `src/frontend/web/src/app/(app)/(interface)/chat/contants.ts`
  Delete. Remove Claude built-in ids and runtime constants from generic chat.
- `src/frontend/web/src/app/(app)/(interface)/chat/components/split-layout.tsx`
  Delete if `/chat` no longer renders file/chat split panes.
- `src/frontend/web/src/app/(app)/(interface)/chat/components/user-input/chat-info-popover.tsx`
  Delete. Claude-only runtime info no longer belongs in generic chat.
- `src/frontend/web/src/app/(app)/(interface)/chat/lib/search_command.ts`
  Delete. Claude slash-command search does not belong in generic chat.
- `src/frontend/web/src/app/(app)/(interface)/chat/lib/search_file.ts`
  Delete. Claude file-sidecar search does not belong in generic chat.
- `src/frontend/web/src/app/(app)/(interface)/chat/page.css`
  Delete if it only styles the removed split/file layout.
- `src/frontend/web/src/api/openapi.d.ts`
  Regenerated frontend API types after backend contract changes.

### Task 1: Shrink `ProjectTask` and Remove Manual Task-Mutation APIs

**Files:**
- Modify: `src/backend/Agw.Shared/Contracts/Tasks/Entities/ProjectTask.cs`
- Modify: `src/backend/Agw.Shared/Contracts/ProjectTaskRequests.cs`
- Modify: `src/backend/Agw.Shared/Contracts/Tasks/ITaskAppService.cs`
- Modify: `src/backend/Agw.Infrastructure/Data/LlmDbContext.cs`
- Modify: `src/backend/Agw.Tasks/DomainServices/ProjectTaskDomainService.cs`
- Modify: `src/backend/Agw.Tasks/Services/ProjectTaskAppService.cs`
- Modify: `src/backend/Agw.Tasks/Services/TaskAppService.cs`
- Modify: `src/backend/Agw.Tasks/Controllers/ProjectTasksController.cs`
- Modify: `tests/Agw.Tasks.Tests/ProjectTaskDomainServiceTests.cs`
- Modify: `tests/Agw.Tasks.Tests/ProjectTaskAppServiceTests.cs`
- Create: `tests/Agw.Tasks.Tests/TaskAppServiceTests.cs`
- Create: `src/backend/Agw.Infrastructure/Migrations/20260407180000_ProjectTaskGeneralization.cs`
- Create: `src/backend/Agw.Infrastructure/Migrations/20260407180000_ProjectTaskGeneralization.Designer.cs`
- Modify: `src/backend/Agw.Infrastructure/Migrations/LlmDbContextModelSnapshot.cs`

- [ ] **Step 1: Write the failing backend tests**

```csharp
// tests/Agw.Tasks.Tests/ProjectTaskDomainServiceTests.cs
[Fact]
public void TryPrepareForCreate_ValidTask_InitializesTaskWithoutDescriptionOrAgent()
{
    var before = DateTime.UtcNow;
    var task = new ProjectTask
    {
        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Title = "  New task  ",
        ContextId = "context-1",
    };
    var initialRecord = new TaskRecord
    {
        TaskId = Guid.NewGuid(),
        ConversationPayload = JsonSerializer.Serialize(new ChatMessage(ChatRole.User, "hello"), JsonOptions),
    };

    var result = _service.TryPrepareForCreate(task, initialRecord, "tester");

    Assert.True(result);
    Assert.Equal("New task", task.Title);
    Assert.Equal(ProjectTaskStatus.Pending, task.Status);
    Assert.Equal(Constants.DefaultAuthor, initialRecord.AgentName);
    Assert.InRange(task.CreateTime, before, DateTime.UtcNow);
}

// tests/Agw.Tasks.Tests/ProjectTaskAppServiceTests.cs
[Fact]
public async Task CreateRunningAsync_PersistsJobIdAndReturnsTitleOnlySummary()
{
    var jobId = Guid.NewGuid();

    var result = await service.CreateRunningAsync(
        projectId,
        new ProjectTaskCreateRequest(
            JobId: jobId,
            Input: "Run scheduled sync",
            Title: "Nightly sync",
            ContextId: "context-1"),
        "job-executor");

    var response = Assert.IsType<ProjectTaskResponse>(result.Value);
    Assert.Equal(jobId, response.JobId);
    Assert.Equal("Nightly sync", response.Title);
    Assert.Null(typeof(ProjectTaskResponse).GetProperty("Description"));
    Assert.Null(typeof(ProjectTaskResponse).GetProperty("AgentType"));
}

// tests/Agw.Tasks.Tests/TaskAppServiceTests.cs
[Fact]
public async Task CreateTaskForExecutionAsync_CreatesChatTaskWithoutTargetBinding()
{
    var task = await service.CreateTaskForExecutionAsync(
        projectId,
        taskId: null,
        input: "  hello world  ",
        user: "tester",
        cancellationToken);

    Assert.NotNull(task);
    Assert.Null(task!.JobId);
    Assert.Equal("hello world", task.Title);
}
```

- [ ] **Step 2: Run the focused test set to verify it fails**

Run:

```bash
dotnet test tests/Agw.Tasks.Tests/Agw.Tasks.Tests.csproj --filter "FullyQualifiedName~ProjectTaskDomainServiceTests|FullyQualifiedName~ProjectTaskAppServiceTests|FullyQualifiedName~TaskAppServiceTests"
```

Expected: FAIL with compile errors or assertions referencing removed `Description` and `AgentId` requirements, plus the old `CreateTaskForExecutionAsync` signature.

- [ ] **Step 3: Implement the session-only task model and remove manual mutation endpoints**

```csharp
// src/backend/Agw.Shared/Contracts/Tasks/Entities/ProjectTask.cs
public class ProjectTask : BaseEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }
    public string ContextId { get; set; } = string.Empty;
    public Guid? JobId { get; set; }
    public string Title { get; set; } = "Untitled";
    public ProjectTaskStatus Status { get; set; } = ProjectTaskStatus.Pending;
    public string? ErrorMessage { get; set; }
    public DateTime? FinishedTime { get; set; }
}

// src/backend/Agw.Shared/Contracts/ProjectTaskRequests.cs
public record ProjectTaskCreateRequest(
    Guid? JobId,
    string Input,
    string? Title = null,
    string? ContextId = null);

public record ProjectTaskSummaryResponse(
    Guid Id,
    string ProjectId,
    string ContextId,
    Guid? JobId,
    ProjectTaskStatus Status,
    string Title,
    string? ErrorMessage,
    DateTime CreateTime,
    DateTime? UpdateTime,
    DateTime? FinishedTime,
    DateTime? StartedTime);

public record ProjectTaskResponse(
    Guid Id,
    string ProjectId,
    string ContextId,
    Guid? JobId,
    ProjectTaskStatus Status,
    string Title,
    string Input,
    string? ErrorMessage,
    DateTime CreateTime,
    DateTime? UpdateTime,
    DateTime? StartedTime,
    DateTime? FinishedTime,
    int MessageCount,
    IReadOnlyList<AgwMessage>? Messages);
```

```csharp
// src/backend/Agw.Shared/Contracts/Tasks/ITaskAppService.cs
Task<ProjectTask?> CreateTaskForExecutionAsync(
    Guid projectId,
    Guid? taskId,
    string input,
    string user,
    CancellationToken cancellationToken = default);
```

```csharp
// src/backend/Agw.Tasks/DomainServices/ProjectTaskDomainService.cs
public bool TryPrepareForCreate(
    ProjectTask task,
    TaskRecord initialRecord,
    string user,
    ProjectTaskStatus initialStatus = ProjectTaskStatus.Pending)
{
    if (string.IsNullOrWhiteSpace(task.ContextId) || string.IsNullOrWhiteSpace(initialRecord.GetText()))
    {
        return false;
    }

    task.Id = task.Id == Guid.Empty ? Guid.NewGuid() : task.Id;
    task.Title = string.IsNullOrWhiteSpace(task.Title) ? "Untitled" : task.Title.Trim();
    task.Status = initialStatus;
    task.CreateBy = user;
    task.CreateTime = DateTime.UtcNow;
    task.UpdateBy = user;
    task.UpdateTime = task.CreateTime;
    initialRecord.TaskId = task.Id;
    initialRecord.AgentName = Constants.DefaultAuthor;
    initialRecord.CreateTime = task.CreateTime;
    initialRecord.UpdateTime = task.CreateTime;
    return true;
}
```

```csharp
// src/backend/Agw.Tasks/Services/ProjectTaskAppService.cs
var task = new ProjectTask
{
    Id = taskId,
    ProjectId = project.Id,
    ContextId = contextId,
    JobId = request.JobId,
    Title = request.Title ?? string.Empty,
};

private static ProjectTaskSummaryResponse ToSummaryResponse(ProjectTask task) =>
    new(
        task.Id,
        task.ProjectId.Normalize(),
        task.ContextId,
        task.JobId,
        task.Status,
        task.Title,
        task.ErrorMessage,
        task.CreateTime,
        task.UpdateTime,
        task.FinishedTime,
        GetStartedTime(task));
```

```csharp
// src/backend/Agw.Tasks/Controllers/ProjectTasksController.cs
[HttpGet]
public async Task<IActionResult> ListAsync(Guid projectId) => Ok(await _projectTaskAppService.ListResponsesAsync(projectId));

[HttpGet("{taskId:guid}")]
public async Task<IActionResult> GetAsync(Guid projectId, Guid taskId)
{
    var task = await _projectTaskAppService.GetResponseAsync(projectId, taskId);
    return task == null ? NotFound() : Ok(task);
}

[HttpDelete("{taskId:guid}")]
public async Task<IActionResult> DeleteAsync(Guid projectId, Guid taskId)
{
    var result = await _projectTaskAppService.DeleteAsync(projectId, taskId);
    return result.Type == ApplicationResultType.Success ? Ok() : NotFound();
}
```

Delete `ProjectTaskUpdateRequest`, `ProjectTaskReorderRequest`, `ProjectTaskDomainService.TryApplyUpdate`, `TryReorder`, `TryCancel`, `GetNextPending`, and the matching `ProjectTaskAppService` plus controller methods that expose them.

- [ ] **Step 4: Generate and inspect the migration**

Run:

```bash
dotnet ef migrations add ProjectTaskGeneralization -p src/backend/Agw.Infrastructure -s src/backend/Agw.Host
```

Expected: EF creates `ProjectTaskGeneralization` migration files and updates `LlmDbContextModelSnapshot.cs`.

Inspect the generated migration and ensure it contains:

```csharp
migrationBuilder.DropColumn(name: "agent_type", table: "project_tasks");
migrationBuilder.DropColumn(name: "agent_id", table: "project_tasks");
migrationBuilder.DropColumn(name: "description", table: "project_tasks");
migrationBuilder.AddColumn<Guid>(name: "job_id", table: "project_tasks", type: "TEXT", nullable: true);
```

- [ ] **Step 5: Run the focused backend tests again**

Run:

```bash
dotnet test tests/Agw.Tasks.Tests/Agw.Tasks.Tests.csproj --filter "FullyQualifiedName~ProjectTaskDomainServiceTests|FullyQualifiedName~ProjectTaskAppServiceTests|FullyQualifiedName~TaskAppServiceTests"
```

Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add tests/Agw.Tasks.Tests/ProjectTaskDomainServiceTests.cs tests/Agw.Tasks.Tests/ProjectTaskAppServiceTests.cs tests/Agw.Tasks.Tests/TaskAppServiceTests.cs src/backend/Agw.Shared/Contracts/Tasks/Entities/ProjectTask.cs src/backend/Agw.Shared/Contracts/ProjectTaskRequests.cs src/backend/Agw.Shared/Contracts/Tasks/ITaskAppService.cs src/backend/Agw.Infrastructure/Data/LlmDbContext.cs src/backend/Agw.Tasks/DomainServices/ProjectTaskDomainService.cs src/backend/Agw.Tasks/Services/ProjectTaskAppService.cs src/backend/Agw.Tasks/Services/TaskAppService.cs src/backend/Agw.Tasks/Controllers/ProjectTasksController.cs src/backend/Agw.Infrastructure/Migrations
git commit -m "refactor(tasks): make project tasks session based"
```

### Task 2: Preserve Per-Turn Target Metadata and Job Snapshots

**Files:**
- Create: `src/backend/Agw.Tasks/DomainServices/ProjectTaskTitleFactory.cs`
- Create: `src/backend/Agw.Tasks/DomainServices/TaskRecordMetadataFactory.cs`
- Modify: `src/backend/Agw.Tasks/Services/TaskAppService.cs`
- Modify: `src/backend/Agw.Agents/Execution/AgentExecutionCoordinator.cs`
- Modify: `src/backend/Agw.Agents/Application/AgentExecSession.cs`
- Modify: `src/backend/Agw.Tasks/DomainServices/EfCoreChatHistoryProvider.cs`
- Modify: `src/backend/Agw.Jobs/Services/AgentExecutor.cs`
- Create: `tests/Agw.Tasks.Tests/TaskRecordMetadataFactoryTests.cs`

- [ ] **Step 1: Write the failing helper tests**

```csharp
// tests/Agw.Tasks.Tests/TaskRecordMetadataFactoryTests.cs
[Fact]
public void FromMessage_CopiesTargetMetadataFromTextContent()
{
    var message = new ChatMessage(
        ChatRole.User,
        new TextContent("hello")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["targetType"] = "agentflow",
                ["targetId"] = "11111111-1111-1111-1111-111111111111"
            }
        });

    var metadata = TaskRecordMetadataFactory.FromMessage(message);

    Assert.NotNull(metadata);
    Assert.Equal("agentflow", metadata!["targetType"].GetString());
    Assert.Equal("11111111-1111-1111-1111-111111111111", metadata["targetId"].GetString());
}

[Fact]
public void Create_UsesTrimmedInputPrefix()
{
    var title = ProjectTaskTitleFactory.Create("  this is a chat title  ");

    Assert.Equal("this is a chat title", title);
}
```

- [ ] **Step 2: Run the new helper test file**

Run:

```bash
dotnet test tests/Agw.Tasks.Tests/Agw.Tasks.Tests.csproj --filter "FullyQualifiedName~TaskRecordMetadataFactoryTests"
```

Expected: FAIL because the helper classes do not exist yet.

- [ ] **Step 3: Implement the metadata pipeline from websocket input to `TaskRecord.Metadata`**

```csharp
// src/backend/Agw.Tasks/DomainServices/ProjectTaskTitleFactory.cs
public static class ProjectTaskTitleFactory
{
public static string Create(string? text, string fallback = "New Chat")
{
    var trimmed = text?.Trim();
    if (string.IsNullOrWhiteSpace(trimmed))
    {
        return fallback;
    }

        return trimmed[..Math.Min(trimmed.Length, 80)];
    }
}

// src/backend/Agw.Tasks/DomainServices/TaskRecordMetadataFactory.cs
public static class TaskRecordMetadataFactory
{
    public static Dictionary<string, JsonElement>? FromMessage(ChatMessage message)
    {
        var props = message.Contents
            .OfType<TextContent>()
            .Select(content => content.AdditionalProperties)
            .FirstOrDefault(dict =>
                dict != null &&
                dict.ContainsKey("targetType") &&
                dict.ContainsKey("targetId"));

        if (props == null)
        {
            return null;
        }

        return new Dictionary<string, JsonElement>
        {
            ["targetType"] = JsonSerializer.SerializeToElement(props["targetType"]),
            ["targetId"] = JsonSerializer.SerializeToElement(props["targetId"]),
        };
    }
}
```

```csharp
// src/backend/Agw.Tasks/Services/TaskAppService.cs
public async Task<ProjectTask?> CreateTaskForExecutionAsync(
    Guid projectId,
    Guid? taskId,
    string input,
    string user,
    CancellationToken cancellationToken = default)
{
    var normalizedInput = input.Trim();
    var request = new ProjectTaskCreateRequest(
        JobId: null,
        Input: normalizedInput,
        Title: ProjectTaskTitleFactory.Create(normalizedInput));

    var result = await _projectTaskAppService.CreateForExecutionAsync(projectId, taskId, request, user);
    return result.Value == null ? null : await _taskRepository.GetByIdAsync(result.Value.Id);
}
```

```csharp
// src/backend/Agw.Agents/Execution/AgentExecutionCoordinator.cs
var task = await _taskAppService.CreateTaskForExecutionAsync(
    projectId,
    taskId,
    input,
    user,
    cancellationToken);
```

```csharp
// src/backend/Agw.Agents/Application/AgentExecSession.cs
private static List<AIContent> ConvertToAIContents(List<AgwContent> contents)
{
    var aiContents = new List<AIContent>();

    foreach (var item in contents)
    {
        switch (item)
        {
            case AgwTextContent text:
                aiContents.Add(new TextContent(text.Content)
                {
                    AdditionalProperties = text.AdditionalProperties == null
                        ? null
                        : new AdditionalPropertiesDictionary(text.AdditionalProperties)
                });
                break;
            case AgwUriContent uri:
                aiContents.Add(new UriContent(uri.Uri, uri.MediaType)
                {
                    AdditionalProperties = uri.AdditionalProperties == null
                        ? null
                        : new AdditionalPropertiesDictionary(uri.AdditionalProperties)
                });
                break;
        }
    }

    return aiContents;
}
```

```csharp
// src/backend/Agw.Tasks/DomainServices/EfCoreChatHistoryProvider.cs
projectTask = new ProjectTask
{
    Id = taskId,
    ProjectId = state.ProjectId,
    ContextId = state.ContextId,
    JobId = null,
    Title = ProjectTaskTitleFactory.Create(firstUserText),
    Status = ProjectTaskStatus.Succeeded,
    FinishedTime = now,
    CreateBy = DefaultUser,
    CreateTime = now,
    UpdateBy = DefaultUser,
    UpdateTime = now
};

dbContext.Set<TaskRecord>().Add(new TaskRecord
{
    Id = Guid.NewGuid(),
    TaskId = taskGuid,
    AgentName = message.AuthorName,
    ConversationSequence = nextSequence,
    ConversationPayload = JsonSerializer.Serialize(message, _jsonSerializerOptions),
    Metadata = TaskRecordMetadataFactory.FromMessage(message),
    CreateTime = now,
    UpdateTime = now
});
```

```csharp
// src/backend/Agw.Jobs/Services/AgentExecutor.cs
var createResult = await projectTaskAppService.CreateRunningAsync(
    job.ProjectId,
    new ProjectTaskCreateRequest(
        JobId: job.Id,
        Input: prompt,
        Title: string.IsNullOrWhiteSpace(job.Name)
            ? ProjectTaskTitleFactory.Create(prompt, "Scheduled Job")
            : job.Name.Trim(),
        ContextId: contextId),
    JobExecutorUser);
```

- [ ] **Step 4: Re-run the focused backend tests**

Run:

```bash
dotnet test tests/Agw.Tasks.Tests/Agw.Tasks.Tests.csproj --filter "FullyQualifiedName~TaskRecordMetadataFactoryTests|FullyQualifiedName~TaskAppServiceTests|FullyQualifiedName~ProjectTaskAppServiceTests"
```

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add tests/Agw.Tasks.Tests/TaskRecordMetadataFactoryTests.cs tests/Agw.Tasks.Tests/TaskAppServiceTests.cs src/backend/Agw.Tasks/DomainServices/ProjectTaskTitleFactory.cs src/backend/Agw.Tasks/DomainServices/TaskRecordMetadataFactory.cs src/backend/Agw.Tasks/Services/TaskAppService.cs src/backend/Agw.Agents/Execution/AgentExecutionCoordinator.cs src/backend/Agw.Agents/Application/AgentExecSession.cs src/backend/Agw.Tasks/DomainServices/EfCoreChatHistoryProvider.cs src/backend/Agw.Jobs/Services/AgentExecutor.cs
git commit -m "refactor(execution): record target metadata per chat turn"
```

### Task 3: Remove the Project-Task Scheduler and Dead Queue Helpers

**Files:**
- Modify: `src/backend/Agw.Jobs/DependencyInjection.cs`
- Delete: `src/backend/Agw.Jobs/HostedService/ProjectTaskSchedulerHostedService.cs`
- Modify: `src/backend/Agw.Tasks/Services/ProjectTaskAppService.cs`
- Modify: `tests/Agw.Tasks.Tests/ProjectTaskDomainServiceTests.cs`

- [ ] **Step 1: Remove the queue-only service registration**

```csharp
// src/backend/Agw.Jobs/DependencyInjection.cs
public static IServiceCollection AddJobs(this IServiceCollection services, IConfiguration configuration)
{
    services.AddHostedService<JobHostedService>();
    services.AddScoped<IAgentExecutor, AgentExecutor>();
    services.AddSingleton<IJobTimeCalculator, JobTimeCalculator>();
    services.AddScoped<JobAppService>();
    return services;
}
```

- [ ] **Step 2: Delete the obsolete scheduler file and the unused app-service helpers**

Delete `src/backend/Agw.Jobs/HostedService/ProjectTaskSchedulerHostedService.cs`.

Remove these unused `ProjectTaskAppService` methods:

```csharp
public Task<bool> HasRunningTaskAsync(Guid projectId);
public Task<ProjectTask?> GetNextPendingAsync(Guid projectId);
public Task<ProjectTask?> TryMarkRunningAsync(Guid id, string user);
```

Also remove the matching test cases in `tests/Agw.Tasks.Tests/ProjectTaskDomainServiceTests.cs` that only exist to exercise `TryReorder`, `TryCancel`, `TryMarkRunning`, or `GetNextPending`.

- [ ] **Step 3: Run a full backend build to catch lingering scheduler references**

Run:

```bash
dotnet build Agw.slnx
```

Expected: PASS with no references to `ProjectTaskSchedulerHostedService`, `GetNextPendingAsync`, or `TryMarkRunningAsync`.

- [ ] **Step 4: Commit**

```bash
git add src/backend/Agw.Jobs/DependencyInjection.cs src/backend/Agw.Tasks/Services/ProjectTaskAppService.cs tests/Agw.Tasks.Tests/ProjectTaskDomainServiceTests.cs src/backend/Agw.Jobs/HostedService/ProjectTaskSchedulerHostedService.cs
git commit -m "refactor(jobs): remove project task scheduler"
```

### Task 4: Keep A2A Task Persistence Working With the New Task Shape

**Files:**
- Modify: `src/backend/Agw.A2A/TaskStore.cs`
- Modify: `src/backend/Agw.A2A/AgentExecutionBridge.cs`
- Modify: `tests/Agw.A2A.Tests/TaskStoreTests.cs`

- [ ] **Step 1: Add a failing A2A regression assertion**

```csharp
// tests/Agw.A2A.Tests/TaskStoreTests.cs
[Fact]
public async Task SaveTaskAsync_PersistsA2ATaskWithoutDescriptionOrTargetBinding()
{
    var taskId = Guid.NewGuid().ToString("D");
    var task = CreateTask(
        taskId: taskId,
        contextId: "ctx-a2a",
        state: TaskState.Working,
        timestamp: new DateTimeOffset(2026, 4, 6, 0, 0, 0, TimeSpan.Zero),
        historyTexts: ["hello"],
        artifactTexts: []);

    await store.SaveTaskAsync(taskId, task, cancellationToken);

    var persistedTask = await dbContext.ProjectTasks.SingleAsync(x => x.Id == Guid.Parse(taskId), cancellationToken);
    Assert.Equal(ProjectDefaults.A2AId, persistedTask.ProjectId);
    Assert.Equal("hello", persistedTask.Title);
    Assert.Null(persistedTask.JobId);
}
```

- [ ] **Step 2: Run the A2A test file**

Run:

```bash
dotnet test tests/Agw.A2A.Tests/Agw.A2A.Tests.csproj --filter "FullyQualifiedName~TaskStoreTests"
```

Expected: FAIL because `TaskStore` and `AgentExecutionBridge` still assign removed `ProjectTask` properties.

- [ ] **Step 3: Update A2A persistence and session bootstrap**

```csharp
// src/backend/Agw.A2A/TaskStore.cs
existingTask = new ProjectTask
{
    Id = taskGuid,
    ProjectId = ProjectDefaults.A2AId,
    ContextId = string.IsNullOrWhiteSpace(task.ContextId) ? taskGuid.Normalize() : task.ContextId.Trim(),
    JobId = null,
    Title = BuildTitle(firstUserText),
    Status = coarseStatus,
    ErrorMessage = statusMessageText,
    CreateBy = SystemUser,
    CreateTime = statusTimestampUtc,
    UpdateBy = SystemUser,
    UpdateTime = statusTimestampUtc,
    FinishedTime = IsTerminal(coarseStatus) ? statusTimestampUtc : null
};
```

```csharp
// src/backend/Agw.A2A/AgentExecutionBridge.cs
var projectTask = new ProjectTask
{
    Id = taskId,
    ProjectId = ProjectDefaults.A2AId,
    ContextId = context.ContextId,
    JobId = null,
    Title = agent.Name,
    CreateBy = Constants.DefaultAuthor,
    CreateTime = DateTime.UtcNow
};
```

- [ ] **Step 4: Re-run the A2A test file**

Run:

```bash
dotnet test tests/Agw.A2A.Tests/Agw.A2A.Tests.csproj --filter "FullyQualifiedName~TaskStoreTests"
```

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/backend/Agw.A2A/TaskStore.cs src/backend/Agw.A2A/AgentExecutionBridge.cs tests/Agw.A2A.Tests/TaskStoreTests.cs
git commit -m "refactor(a2a): align persisted tasks with session model"
```

### Task 5: Convert Project Task Pages Into Read-Only History

**Files:**
- Modify: `src/frontend/web/src/api/task-client.ts`
- Modify: `src/frontend/web/src/app/(app)/(tasks)/projects/[id]/page.tsx`
- Modify: `src/frontend/web/src/app/(app)/(tasks)/projects/[id]/tasks/[taskId]/page.tsx`
- Modify: `src/frontend/web/src/app/(app)/(tasks)/projects/[id]/tasks/[taskId]/types.ts`
- Delete: `src/frontend/web/src/app/(app)/(tasks)/projects/[id]/components/create-task-dialog.tsx`

- [ ] **Step 1: Tighten the frontend task DTOs first**

```ts
// src/frontend/web/src/api/task-client.ts
export type ProjectTaskSummaryResponse = {
  id: string;
  projectId: string;
  contextId: string;
  jobId?: string | null;
  status?: number;
  title: string;
  errorMessage?: string | null;
  createTime: string;
  updateTime?: string | null;
  startedTime?: string | null;
  finishedTime?: string | null;
};
```

```ts
// src/frontend/web/src/app/(app)/(tasks)/projects/[id]/tasks/[taskId]/types.ts
import type { AiMessage } from "@/types";

export type ProjectTaskDto = {
  id: string;
  projectId: string;
  contextId: string;
  jobId?: string | null;
  status: number;
  title: string;
  input: string;
  messageCount: number;
  messages?: AiMessage[] | null;
  errorMessage?: string | null;
  createTime?: string | null;
  updateTime?: string | null;
  startedTime?: string | null;
  finishedTime?: string | null;
};
```

- [ ] **Step 2: Run typecheck to expose all old task-management references**

Run:

```bash
pnpm exec tsc --noEmit
```

Expected: FAIL with errors such as `Property 'description' does not exist`, `Property 'agentType' does not exist`, or imports from the soon-to-be-deleted `create-task-dialog.tsx`.

- [ ] **Step 3: Remove create/edit/reorder/cancel UI and render read-only history**

```tsx
// src/frontend/web/src/app/(app)/(tasks)/projects/[id]/page.tsx
<CardTitle>Tasks</CardTitle>
<CardDescription>Read-only history for chat sessions and job runs.</CardDescription>

{tasks.map((t) => (
  <div key={t.id} className="rounded-lg border p-4">
    <div className="flex flex-wrap items-center gap-2">
      <div className="font-medium">{t.title}</div>
      <span className={`rounded-md px-2 py-0.5 text-xs ${statusClassName(t.status)}`}>
        {statusLabel(t.status)}
      </span>
    </div>
    <div className="text-xs text-muted-foreground">
      <span className="font-mono">{t.id}</span>
      <span className="mx-2">·</span>
      Source: <span className="font-mono">{t.jobId ? `job:${t.jobId}` : "chat"}</span>
    </div>
  </div>
))}
```

```tsx
// src/frontend/web/src/app/(app)/(tasks)/projects/[id]/tasks/[taskId]/page.tsx
const messagesStartRef = React.useRef<HTMLDivElement>(null!);
const messagesEndRef = React.useRef<HTMLDivElement>(null!);

<h1 className="truncate text-xl font-semibold">
  {taskQuery.isLoading ? "Loading task..." : (task?.title ?? "Task")}
</h1>

<div className="text-sm text-muted-foreground">
  {task?.jobId ? `Source job: ${task.jobId}` : "Source: chat"}
</div>

<Conversation
  taskId={task?.id ?? taskId}
  messages={task?.messages ?? []}
  messagesStartRef={messagesStartRef}
  messagesEndRef={messagesEndRef}
/>

<Button asChild variant="outline" size="sm">
  <Link href={`/chat?projectId=${projectId}&taskId=${taskId}`}>Continue In Chat</Link>
</Button>
```

Delete the `CreateTaskDialog` import, all task create/update/reorder/cancel mutations, the edit-task dialog, and the target-derived `Agent` or `Agentflow` link from the task detail page.

- [ ] **Step 4: Run focused frontend verification**

Run:

```bash
pnpm exec tsc --noEmit
pnpm exec oxlint "src/app/(app)/(tasks)/projects/[id]/page.tsx" "src/app/(app)/(tasks)/projects/[id]/tasks/[taskId]/page.tsx" "src/api/task-client.ts"
```

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/frontend/web/src/api/task-client.ts src/frontend/web/src/app/(app)/(tasks)/projects/[id]/page.tsx src/frontend/web/src/app/(app)/(tasks)/projects/[id]/tasks/[taskId]/page.tsx src/frontend/web/src/app/(app)/(tasks)/projects/[id]/tasks/[taskId]/types.ts src/frontend/web/src/app/(app)/(tasks)/projects/[id]/components/create-task-dialog.tsx
git commit -m "refactor(ui): make project tasks read only"
```

### Task 6: Make `/chat` Target-Driven and Remove Claude-Specific UI

**Files:**
- Modify: `src/frontend/web/src/components/task/task-list.tsx`
- Modify: `src/frontend/web/src/app/(app)/(external-agents)/claude-code/page.tsx`
- Modify: `src/frontend/web/src/app/(app)/(interface)/chat/page.tsx`
- Modify: `src/frontend/web/src/app/(app)/(interface)/chat/types.ts`
- Modify: `src/frontend/web/src/app/(app)/(interface)/chat/components/user-input/input-area.tsx`
- Modify: `src/frontend/web/src/app/(app)/(interface)/chat/lib/ai-message-handlers.ts`
- Delete: `src/frontend/web/src/app/(app)/(interface)/chat/contants.ts`
- Delete: `src/frontend/web/src/app/(app)/(interface)/chat/components/split-layout.tsx`
- Delete: `src/frontend/web/src/app/(app)/(interface)/chat/components/user-input/chat-info-popover.tsx`
- Delete: `src/frontend/web/src/app/(app)/(interface)/chat/lib/search_command.ts`
- Delete: `src/frontend/web/src/app/(app)/(interface)/chat/lib/search_file.ts`
- Delete: `src/frontend/web/src/app/(app)/(interface)/chat/page.css`

- [ ] **Step 1: Make the shared history sidebar generic first**

```tsx
// src/frontend/web/src/components/task/task-list.tsx
interface TaskHistoryListProps {
  projectId: string;
  currentTaskId: string | null;
  onTaskSelect: (taskId: string) => void;
  onNewTask: () => void;
  onTaskDeleted: (taskId: string) => void;
  onAllTasksDeleted: () => void;
  headerActions?: React.ReactNode;
}

// inside the header
<div className="tools">
  <Button ... onClick={refreshTasks}><RotateCw ... /></Button>
  <Button ... onClick={async () => { await Promise.resolve(onNewTask()); await refreshTasks(); }}>
    <Plus className="h-4 w-4" />
  </Button>
  <Button ... onClick={() => setInfoModalOpen(true)}><Info className="h-4 w-4" /></Button>
  {headerActions}
</div>
```

Update `src/frontend/web/src/app/(app)/(external-agents)/claude-code/page.tsx` to pass its existing Claude settings button through `headerActions` so the external Claude page keeps working.

- [ ] **Step 2: Rewrite the chat-local types around target selection**

```ts
// src/frontend/web/src/app/(app)/(interface)/chat/types.ts
import type { UserInputRef } from "@/components/message/user-input";

export type ChatTargetType = "agent" | "agentflow";

export type ChatTargetOption = {
  id: string;
  label: string;
  type: ChatTargetType;
};

export interface ChatInputAreaProps {
  isExecuting: boolean;
  hasMessages: boolean;
  onExecute: (value: string) => void;
  onInterrupt: () => void;
  onClearSession: () => void;
  onScrollToTop: () => void;
  userInputRef?: React.RefObject<UserInputRef | null>;
}
```

- [ ] **Step 3: Run typecheck to surface every remaining Claude-specific dependency**

Run:

```bash
pnpm exec tsc --noEmit
```

Expected: FAIL with unresolved references to `claudeSettingsStorage`, `CLAUDE_CODE_PROJECT_ID`, `ChatInfoPopover`, `searchCommand`, `searchFile`, `split-layout`, or the removed `TaskHistoryList` settings props.

- [ ] **Step 4: Implement the generic `/chat` page and target switching behavior**

```tsx
// src/frontend/web/src/app/(app)/(interface)/chat/page.tsx
if (!selectedTarget) {
  toast.error("Please select a target");
  return;
}

const agentsQuery = useQuery({
  queryKey: ["agents"],
  queryFn: async () =>
    (await apiGet("/api/agents")) as Array<{ id: string; displayName: string; name: string }>,
});

const agentflowsQuery = useQuery({
  queryKey: ["agentflows"],
  queryFn: async () => (await apiGet("/api/agentflows")) as Array<{ id: string; name: string }>,
});

const selectedTarget = React.useMemo(
  () => targetOptions.find((option) => option.id === selectedTargetId) ?? null,
  [targetOptions, selectedTargetId],
);

const buildSettingRequest = (nextTaskId: string) => ({
  type: "SettingCommand",
  settingContent: "{}",
  projectId: selectedProjectId,
  taskId: nextTaskId,
});

const buildExecRequest = (message: AiMessage) => ({
  type: "ExecCommand",
  agentType: selectedTarget?.type === "agent" ? 0 : 1,
  input: toExecutionWsUserInput(message),
});
```

```tsx
// top bar in page.tsx
<div className="flex flex-wrap items-center gap-2">
  <Select value={projectSelectValue} onValueChange={handleProjectChange}>
    <SelectTrigger className="w-[220px]" aria-label="Select project">
      <SelectValue placeholder="Select project" />
    </SelectTrigger>
    <SelectContent position="popper" side="bottom" align="start" sideOffset={4}>
      {projects.map((project) => (
        <SelectItem key={project.id} value={project.id}>
          {project.name}
        </SelectItem>
      ))}
    </SelectContent>
  </Select>

  <Select value={selectedTargetId ?? undefined} onValueChange={handleTargetChange}>
    <SelectTrigger className="w-[260px]" aria-label="Select target">
      <SelectValue placeholder="Select agent or agentflow" />
    </SelectTrigger>
    <SelectContent position="popper" side="bottom" align="start" sideOffset={4}>
      <SelectGroup>
        <SelectLabel>Agent</SelectLabel>
        {targetOptions.filter((option) => option.type === "agent").map((option) => (
          <SelectItem key={option.id} value={option.id}>{option.label}</SelectItem>
        ))}
      </SelectGroup>
      <SelectGroup>
        <SelectLabel>Agentflow</SelectLabel>
        {targetOptions.filter((option) => option.type === "agentflow").map((option) => (
          <SelectItem key={option.id} value={option.id}>{option.label}</SelectItem>
        ))}
      </SelectGroup>
    </SelectContent>
  </Select>
</div>
```

```tsx
// target switching rule in page.tsx
const handleTargetChange = React.useCallback((nextTargetId: string) => {
  if (nextTargetId === selectedTargetId) {
    return;
  }

  if (wsRef.current) {
    wsRef.current.close(1000, "Target switched");
    wsRef.current = null;
  }

  setIsExecuting(false);
  setSelectedTargetId(nextTargetId);
}, [selectedTargetId]);
```

```ts
// before sending in page.tsx
const userMsg = createUserTextMessage(inputMsg);
const firstContent = userMsg.contents[0];
firstContent.additionalProperties = {
  ...(firstContent.additionalProperties ?? {}),
  targetType: selectedTarget?.type,
  targetId: selectedTarget?.id,
};
```

Delete the file tab, comment dialog, file explorer, Claude init popover, Claude constants, Claude slash/file suggestion helpers, and any `claude*` names in the generic `/chat` route. Keep project-scoped history loading, and when `projectId` plus `taskId` are present in the URL query string, hydrate the chat page by calling `getTaskDetails(projectId, taskId)` instead of creating a new task immediately. Loading an existing task must not overwrite the currently selected target.

- [ ] **Step 5: Simplify the input area and system-message handling**

```tsx
// src/frontend/web/src/app/(app)/(interface)/chat/components/user-input/input-area.tsx
export function InputArea({
  isExecuting,
  onExecute,
  onInterrupt,
  onClearSession,
  onScrollToTop,
  userInputRef: externalUserInputRef,
}: ChatInputAreaProps) {
  const internalUserInputRef = useRef<UserInputRef | null>(null);
  const userInputRef = externalUserInputRef ?? internalUserInputRef;

  return (
    <UserInput ref={userInputRef} isExecuting={isExecuting} onExecute={onExecute} onStop={onInterrupt}>
      <UserInput.TopRight>
        <Button onClick={onClearSession} disabled={isExecuting} variant="ghost" size="sm">
          <Eraser width={16} />
        </Button>
        <Separator orientation="vertical" />
        <Button onClick={onScrollToTop} variant="ghost" size="sm">
          <ArrowUp width={16} />
        </Button>
      </UserInput.TopRight>
      {isExecuting ? (
        <UserInput.Sender>
          <Square size={20} />
        </UserInput.Sender>
      ) : null}
    </UserInput>
  );
}
```

```ts
// src/frontend/web/src/app/(app)/(interface)/chat/lib/ai-message-handlers.ts
export const handleAiMessage = (data: AiMessage): AiMessageAction[] => {
  if (data.role === "system" && data.additionalProperties?.type === "result") {
    return [{ type: "append", message: data }, { type: "setIsExecuting", value: false }];
  }

  return [{ type: "append", message: data }];
};
```

- [ ] **Step 6: Run focused frontend verification**

Run:

```bash
pnpm exec tsc --noEmit
pnpm exec oxlint "src/app/(app)/(interface)/chat/page.tsx" "src/app/(app)/(interface)/chat/components/user-input/input-area.tsx" "src/components/task/task-list.tsx"
```

Expected: PASS

- [ ] **Step 7: Commit**

```bash
git add src/frontend/web/src/components/task/task-list.tsx src/frontend/web/src/app/(app)/(external-agents)/claude-code/page.tsx src/frontend/web/src/app/(app)/(interface)/chat
git commit -m "refactor(chat): make chat target driven"
```

### Task 7: Regenerate API Types and Run Full Verification

**Files:**
- Modify: `src/frontend/web/openapi.json`
- Modify: `src/frontend/web/src/api/openapi.d.ts`

- [ ] **Step 1: Refresh the frontend OpenAPI source and regenerate types**

Run:

```bash
dotnet run --project src/backend/Agw.Host
Invoke-WebRequest -Uri http://localhost:5015/openapi/v1.json -OutFile src/frontend/web/openapi.json
cd src/frontend/web
pnpm gen:openapi
```

Expected: `src/frontend/web/src/api/openapi.d.ts` updates to the new task contract shape without `agentType`, `agentId`, `agentflowId`, or `description` on `ProjectTask`.

- [ ] **Step 2: Run the focused backend test projects**

Run:

```bash
dotnet test tests/Agw.Tasks.Tests/Agw.Tasks.Tests.csproj
dotnet test tests/Agw.A2A.Tests/Agw.A2A.Tests.csproj
```

Expected: PASS

- [ ] **Step 3: Run the frontend checks**

Run:

```bash
cd src/frontend/web
pnpm exec tsc --noEmit
pnpm lint
```

Expected: PASS

- [ ] **Step 4: Run the full solution tests**

Run:

```bash
dotnet test Agw.slnx
```

Expected: PASS

- [ ] **Step 5: Manual smoke-check the user flows**

Run:

```bash
dotnet run --project src/backend/Agw.Host
cd src/frontend/web
pnpm dev
```

Expected:

- `/chat` shows `Project Select` plus grouped `Target Select`.
- Switching target closes the websocket immediately but keeps visible messages and the current `taskId`.
- Sending again uses the newly selected target and appends to the same task history.
- `/projects/{id}` shows title, status, timestamps, and `job` or `chat` source markers without edit controls.
- `/projects/{id}/tasks/{taskId}` renders read-only history and the “Continue In Chat” link lands on the same project plus task.

- [ ] **Step 6: Commit**

```bash
git add src/frontend/web/openapi.json src/frontend/web/src/api/openapi.d.ts
git commit -m "chore(api): regenerate task contracts"
```
