# FilesController Service Extraction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract file operations from `FilesController` into an HTTP-independent `FileAppService` while preserving all existing routes and responses.

**Architecture:** `FilesController` remains the HTTP adapter and owns path validation plus outcome-to-response mapping. A concrete `FileAppService` owns file I/O, Git orchestration, search rules, and operation logging, returning typed application outcomes and models without depending on MVC or `Api.Dtos`.

**Tech Stack:** .NET 10, ASP.NET Core MVC, xUnit v3, `System.IO`, existing `IGitCommandService`.

## Global Constraints

- Preserve unrelated staged and unstaged work; do not stage or commit.
- Preserve `/api/files/*` routes, status codes, response property names, messages, defaults, sorting, ignore rules, and existing Git path-prefix semantics.
- Do not introduce `IFileAppService`; one adapter does not justify a new seam.
- Application code must not reference `IActionResult`, MVC status types, or `Agw.Files.Api.Dtos`.
- Follow TDD: add the service tests and observe the expected failure before adding production types.

---

### Task 1: Add the FileAppService application module

**Files:**
- Create: `src/server/Agw.Files/Application/Files/FileOperationResult.cs`
- Create: `src/server/Agw.Files/Application/Files/FileAppModels.cs`
- Create: `src/server/Agw.Files/Application/Files/FileAppService.cs`
- Create: `tests/Agw.Files.Tests/FileAppServiceTests.cs`

**Interfaces:**
- Consumes: `IGitCommandService` and `ILogger<FileAppService>`.
- Produces: `FileAppService.ListAsync`, `ReadAsync`, `DiffAsync`, `DeleteAsync`, `ResetAsync`, and `SearchAsync`.
- Produces: `FileOperationResult<T>` with `Success`, `NotFound`, `InvalidRequest`, and `Failure` statuses.

- [ ] **Step 1: Write failing application tests**

Create tests that directly instantiate the desired module and cover representative behavior:

```csharp
var service = new FileAppService(fakeGit, NullLogger<FileAppService>.Instance);
var result = await service.ListAsync(rootPath, diff: true, recursive: false, cancellationToken);
Assert.Equal(FileOperationStatus.Success, result.Status);
Assert.Collection(result.Value!.Items, /* directory-first and Git status assertions */);
```

Add focused tests for:

- list filtering and deleted entries;
- read success and missing file;
- diff changed, unchanged, and invalid request;
- delete file, directory, and missing path;
- reset success, client error, server failure, and non-error unsuccessful outcome;
- recursive/non-recursive search and existing ignore rules.

- [ ] **Step 2: Run the tests to verify RED**

Run:

```bash
dotnet test tests/Agw.Files.Tests/Agw.Files.Tests.csproj --no-restore --filter "FullyQualifiedName~FileAppServiceTests"
```

Expected: compilation fails because `FileAppService` and its application result types do not exist.

- [ ] **Step 3: Add typed application outcomes and models**

Create an HTTP-independent result interface:

```csharp
public enum FileOperationStatus
{
    Success,
    NotFound,
    InvalidRequest,
    Failure
}

public sealed record FileOperationResult<T>(
    FileOperationStatus Status,
    T? Value,
    string? Message,
    string? Details);
```

Add application models for list entries, list output, search entries, search output, diff output, and mutation output. Keep JSON response DTOs in `Api/Dtos`; the Controller will map between the two sets.

- [ ] **Step 4: Implement FileAppService**

Move the existing Controller implementation without changing behavior:

```csharp
public sealed class FileAppService
{
    public Task<FileOperationResult<FileListOutput>> ListAsync(
        string path,
        bool diff,
        bool recursive,
        CancellationToken cancellationToken = default);

    public Task<FileOperationResult<string>> ReadAsync(
        string path,
        CancellationToken cancellationToken = default);

    public Task<FileOperationResult<FileDiffOutput>> DiffAsync(
        string path,
        CancellationToken cancellationToken = default);

    public Task<FileOperationResult<FileMutationOutput>> DeleteAsync(
        string path,
        CancellationToken cancellationToken = default);

    public Task<FileOperationResult<FileMutationOutput>> ResetAsync(
        string path,
        CancellationToken cancellationToken = default);

    public Task<FileOperationResult<FileSearchOutput>> SearchAsync(
        string path,
        string? keyword,
        int limit,
        bool recursive,
        CancellationToken cancellationToken = default);
}
```

Retain the existing search ignore rules and Git projection semantics exactly. Pass cancellation to asynchronous Git and file reads. Allow unexpected I/O and authorization exceptions to propagate.

- [ ] **Step 5: Run FileAppService tests to verify GREEN**

Run the filtered test command from Step 2.

Expected: all `FileAppServiceTests` pass.

---

### Task 2: Convert FilesController into the HTTP adapter

**Files:**
- Modify: `src/server/Agw.Files/Api/FilesController.cs`
- Modify: `src/server/Agw.Files/DependencyInjection.cs`
- Modify: `tests/Agw.Files.Tests/FilesControllerPathSecurityTests.cs`
- Modify: `tests/Agw.Files.Tests/FilesControllerSearchTests.cs`
- Modify: `tests/Agw.Files.Tests/DependencyInjectionTests.cs`
- Modify: `tests/Agw.Files.Tests/FilesModuleOwnershipTests.cs`

**Interfaces:**
- Consumes: `FileAppService` and `IFilePathRequestValidator`.
- Preserves: all existing MVC action signatures and HTTP response shapes.

- [ ] **Step 1: Add failing adapter and DI assertions**

Assert that the Controller constructor depends on `FileAppService` and no longer depends on `IGitCommandService` or `ILogger<FilesController>`. Assert that `AddFiles` resolves `FileAppService`.

- [ ] **Step 2: Run focused tests to verify RED**

Run:

```bash
dotnet test tests/Agw.Files.Tests/Agw.Files.Tests.csproj --no-restore --filter "FullyQualifiedName~FilesModuleOwnershipTests|FullyQualifiedName~DependencyInjectionTests"
```

Expected: the new constructor and DI assertions fail.

- [ ] **Step 3: Register FileAppService and thin the Controller**

Register the concrete module:

```csharp
services.AddSingleton<FileAppService>();
```

Change the Controller constructor to:

```csharp
public FilesController(
    FileAppService fileAppService,
    IFilePathRequestValidator pathValidator)
```

Keep `TryResolveRequiredPath`. Each action invokes one `FileAppService` method, maps non-success statuses through a private `MapError` helper, and maps successful application models to the existing response DTOs or anonymous payloads.

- [ ] **Step 4: Update Controller test construction**

Construct a real `FileAppService` with the existing Git fake, keeping path-security tests at the Controller seam. Keep search response tests as end-to-end adapter checks while the detailed search behavior lives in `FileAppServiceTests`.

- [ ] **Step 5: Run Agw.Files.Tests to verify GREEN**

Run:

```bash
dotnet test tests/Agw.Files.Tests/Agw.Files.Tests.csproj --no-restore
```

Expected: all Files tests pass.

---

### Task 3: Documentation and repository verification

**Files:**
- Modify: `src/server/Agw.Files/README.zh-CN.md`

**Interfaces:**
- Documents: Controller adapter responsibilities and `FileAppService` application responsibilities.

- [ ] **Step 1: Update module documentation**

Document that `FilesController` performs HTTP mapping while `FileAppService` owns host file and Git operations. Add `FileAppService` to the directory map and extension guidance.

- [ ] **Step 2: Run static checks**

Run:

```bash
rg -n "IGitCommandService|Directory\.|System\.IO\.File|SearchFilesRecursive" src/server/Agw.Files/Api/FilesController.cs
git diff --check
```

Expected: the Controller contains none of the moved implementation dependencies or helpers, and the diff has no whitespace errors.

- [ ] **Step 3: Run the full test suite**

Run:

```bash
dotnet test Agw.slnx --no-restore
```

Expected: all backend tests pass; existing `NU1507` package-source warnings may remain.

- [ ] **Step 4: Review the final diff**

Confirm every changed line belongs to the extraction, existing staged changes remain staged, no migration or generated artifact changed, and no commit was created.
