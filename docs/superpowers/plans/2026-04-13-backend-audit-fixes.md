# Backend Audit Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the first round of backend low-quality audit findings with a narrow, testable backend-only change.

**Architecture:** Add a focused path security service to `Agw.Tasks`, inject it into `FilesController`, and centralize path validation there. Keep controller routes and response shapes stable while replacing audit-pointed generic exceptions and the misspelled runtime base class.

**Tech Stack:** .NET 10, ASP.NET Core controllers, xUnit v3, existing Agw backend projects.

---

## File Structure

- Create: `src/backend/Agw.Tasks/Application/Files/IPathSecurityService.cs`
  - Defines the path resolution contract used by controllers.
- Create: `src/backend/Agw.Tasks/Application/Files/PathSecurityService.cs`
  - Resolves paths under the allowed content root and rejects traversal.
- Modify: `src/backend/Agw.Tasks/DependencyInjection.cs`
  - Registers `IPathSecurityService`.
- Modify: `src/backend/Agw.Tasks/Controllers/FilesController.cs`
  - Injects path security, removes repeated invalid traversal checks, removes `Task.Run` search wrapper.
- Create: `tests/Agw.Tasks.Tests/PathSecurityServiceTests.cs`
  - Covers root, child, relative child, absolute escape, relative escape, and sibling-prefix safety.
- Create: `tests/Agw.Tasks.Tests/FilesControllerPathSecurityTests.cs`
  - Verifies file endpoints reject paths denied by the path security service.
- Move: `src/backend/Agw.Agents/Application/AgentRun/RuntimServiceBase.cs` to `src/backend/Agw.Agents/Application/AgentRun/RuntimeServiceBase.cs`
  - Fixes spelling and updates references.
- Modify: `src/backend/Agw.Agents/Application/AgentRun/AgentRuntimeService.cs`
  - Updates base type name and replaces `throw new Exception("aiAgent not found")`.
- Modify: `src/backend/Agw.Agents/Application/Agentflows/AgentflowRuntimeService.cs`
  - Updates base type name.
- Modify: `src/backend/Agw.A2A/AgwA2ARequestHandler.cs`
  - Replaces generic handler lookup exception with `A2AException`.
- Modify: `src/backend/Agw.Tools/Impl/Files/LsTool.cs`
  - Replaces generic exceptions with precise argument and directory exceptions.
- Modify: `src/backend/Agw.Tools/Impl/Files/ReadFileTool.cs`
  - Replaces generic exceptions with precise argument and file exceptions.
- Modify: `src/backend/Agw.Integrations/Tools/GitHub/GitHubTools.cs`
  - Replaces generic missing OAuth token exception.

---

### Task 1: Path Security Service Tests

**Files:**
- Create: `tests/Agw.Tasks.Tests/PathSecurityServiceTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Agw.Tasks.Tests/PathSecurityServiceTests.cs`:

```csharp
using Agw.Tasks.Application.Files;

namespace Agw.Tasks.Tests;

public class PathSecurityServiceTests
{
    [Fact]
    public void TryResolvePath_WhenPathIsRoot_AllowsRoot()
    {
        using var scope = TempPathScope.Create();
        var service = new PathSecurityService(scope.RootPath);

        var allowed = service.TryResolvePath(scope.RootPath, out var resolvedPath);

        Assert.True(allowed);
        Assert.Equal(Path.GetFullPath(scope.RootPath), resolvedPath);
    }

    [Fact]
    public void TryResolvePath_WhenPathIsChild_AllowsChild()
    {
        using var scope = TempPathScope.Create();
        var childPath = Path.Combine(scope.RootPath, "src", "file.txt");
        var service = new PathSecurityService(scope.RootPath);

        var allowed = service.TryResolvePath(childPath, out var resolvedPath);

        Assert.True(allowed);
        Assert.Equal(Path.GetFullPath(childPath), resolvedPath);
    }

    [Fact]
    public void TryResolvePath_WhenPathIsRelativeChild_AllowsChildUnderRoot()
    {
        using var scope = TempPathScope.Create();
        var service = new PathSecurityService(scope.RootPath);

        var allowed = service.TryResolvePath(Path.Combine("src", "file.txt"), out var resolvedPath);

        Assert.True(allowed);
        Assert.Equal(Path.GetFullPath(Path.Combine(scope.RootPath, "src", "file.txt")), resolvedPath);
    }

    [Fact]
    public void TryResolvePath_WhenPathIsAbsoluteSibling_RejectsPath()
    {
        using var scope = TempPathScope.Create();
        var service = new PathSecurityService(scope.RootPath);
        var sibling = Path.Combine(scope.ParentPath, $"{Path.GetFileName(scope.RootPath)}-outside", "file.txt");

        var allowed = service.TryResolvePath(sibling, out var resolvedPath);

        Assert.False(allowed);
        Assert.Equal(string.Empty, resolvedPath);
    }

    [Fact]
    public void TryResolvePath_WhenPathTraversesToSibling_RejectsPath()
    {
        using var scope = TempPathScope.Create();
        var service = new PathSecurityService(scope.RootPath);
        var relativeTraversal = Path.Combine("..", $"{Path.GetFileName(scope.RootPath)}-outside", "file.txt");

        var allowed = service.TryResolvePath(relativeTraversal, out var resolvedPath);

        Assert.False(allowed);
        Assert.Equal(string.Empty, resolvedPath);
    }

    [Fact]
    public void TryResolvePath_WhenSiblingSharesRootPrefix_RejectsPath()
    {
        using var scope = TempPathScope.Create();
        var service = new PathSecurityService(scope.RootPath);
        var prefixedSibling = scope.RootPath + "-prefixed";

        var allowed = service.TryResolvePath(prefixedSibling, out var resolvedPath);

        Assert.False(allowed);
        Assert.Equal(string.Empty, resolvedPath);
    }

    private sealed class TempPathScope : IDisposable
    {
        private TempPathScope(string rootPath)
        {
            RootPath = rootPath;
            ParentPath = Directory.GetParent(rootPath)?.FullName
                ?? throw new InvalidOperationException("Temporary root must have a parent directory.");
            Directory.CreateDirectory(rootPath);
        }

        public string RootPath { get; }

        public string ParentPath { get; }

        public static TempPathScope Create()
        {
            var rootPath = Path.Combine(Path.GetTempPath(), "agw-path-security-tests", Guid.NewGuid().ToString("N"));
            return new TempPathScope(rootPath);
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify RED**

Run:

```bash
dotnet test tests/Agw.Tasks.Tests/Agw.Tasks.Tests.csproj --filter PathSecurityServiceTests
```

Expected: build fails because `Agw.Tasks.Application.Files.PathSecurityService` does not exist.

---

### Task 2: Path Security Service Implementation

**Files:**
- Create: `src/backend/Agw.Tasks/Application/Files/IPathSecurityService.cs`
- Create: `src/backend/Agw.Tasks/Application/Files/PathSecurityService.cs`
- Modify: `src/backend/Agw.Tasks/DependencyInjection.cs`

- [ ] **Step 1: Add the interface**

Create `src/backend/Agw.Tasks/Application/Files/IPathSecurityService.cs`:

```csharp
namespace Agw.Tasks.Application.Files;

public interface IPathSecurityService
{
    string RootPath { get; }

    bool TryResolvePath(string path, out string resolvedPath);
}
```

- [ ] **Step 2: Add the implementation**

Create `src/backend/Agw.Tasks/Application/Files/PathSecurityService.cs`:

```csharp
using Microsoft.AspNetCore.Hosting;

namespace Agw.Tasks.Application.Files;

public sealed class PathSecurityService : IPathSecurityService
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public PathSecurityService(IWebHostEnvironment webHostEnvironment)
        : this(webHostEnvironment.ContentRootPath)
    {
    }

    public PathSecurityService(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("Root path is required.", nameof(rootPath));
        }

        RootPath = Path.GetFullPath(rootPath);
    }

    public string RootPath { get; }

    public bool TryResolvePath(string path, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var candidatePath = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(RootPath, path));

            if (!IsUnderRoot(candidatePath))
            {
                return false;
            }

            resolvedPath = candidatePath;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
    }

    private bool IsUnderRoot(string candidatePath)
    {
        if (string.Equals(RootPath, candidatePath, PathComparison))
        {
            return true;
        }

        var relativePath = Path.GetRelativePath(RootPath, candidatePath);
        if (relativePath == ".")
        {
            return true;
        }

        return !Path.IsPathRooted(relativePath)
            && !string.Equals(relativePath, "..", PathComparison)
            && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison)
            && !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", PathComparison);
    }
}
```

- [ ] **Step 3: Register the service**

Modify `src/backend/Agw.Tasks/DependencyInjection.cs`:

```csharp
using Agw.Domain.Services;
using Agw.Shared.Contracts.Tasks;
using Agw.Tasks.Application;
using Agw.Tasks.Application.Files;
using Agw.Tasks.Domain.Services;
```

Add this registration before singleton chat history registrations:

```csharp
services.AddSingleton<IPathSecurityService, PathSecurityService>();
```

- [ ] **Step 4: Run the tests to verify GREEN**

Run:

```bash
dotnet test tests/Agw.Tasks.Tests/Agw.Tasks.Tests.csproj --filter PathSecurityServiceTests
```

Expected: all `PathSecurityServiceTests` pass.

---

### Task 3: FilesController Path Validation Refactor

**Files:**
- Create: `tests/Agw.Tasks.Tests/FilesControllerPathSecurityTests.cs`
- Modify: `src/backend/Agw.Tasks/Controllers/FilesController.cs`

- [ ] **Step 1: Write the failing controller tests**

Create `tests/Agw.Tasks.Tests/FilesControllerPathSecurityTests.cs`:

```csharp
using Agw.Shared.Services;
using Agw.Tasks.Application.Files;
using Agw.Tasks.Controllers;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Tasks.Tests;

public class FilesControllerPathSecurityTests
{
    [Fact]
    public async Task ReadAsync_WhenPathIsRejected_ReturnsBadRequest()
    {
        var controller = CreateController(new RejectingPathSecurityService());

        var result = await controller.ReadAsync(Path.Combine("..", "outside.txt"));

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Invalid path", badRequest.Value?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_WhenPathIsRejected_ReturnsBadRequest()
    {
        var controller = CreateController(new RejectingPathSecurityService());

        var result = await controller.SearchAsync(Path.Combine("..", "outside"), "query");

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Invalid path", badRequest.Value?.ToString(), StringComparison.Ordinal);
    }

    private static FilesController CreateController(IPathSecurityService pathSecurityService)
    {
        return new FilesController(
            NullLogger<FilesController>.Instance,
            new FakeGitCommandService(),
            pathSecurityService);
    }

    private sealed class RejectingPathSecurityService : IPathSecurityService
    {
        public string RootPath => Path.GetTempPath();

        public bool TryResolvePath(string path, out string resolvedPath)
        {
            resolvedPath = string.Empty;
            return false;
        }
    }

    private sealed class FakeGitCommandService : IGitCommandService
    {
        public Task<GitChangedFiles?> GetChangedFilesAsync(string directory, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<GitChangedFiles?>(null);
        }

        public Task<GitDiffResult> GetDiffAsync(string filePath, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new GitDiffResult(false, null, null, false, null));
        }

        public Task<GitResetResult> ResetFileAsync(string filePath, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new GitResetResult(false, "Not implemented by test fake.", null, false));
        }

        public Task<GitCloneResult> CloneRepositoryAsync(string gitAddress, string workingDirectory, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new GitCloneResult(false, null, null, "Not implemented by test fake."));
        }
    }
}
```

- [ ] **Step 2: Run the controller tests to verify RED**

Run:

```bash
dotnet test tests/Agw.Tasks.Tests/Agw.Tasks.Tests.csproj --filter FilesControllerPathSecurityTests
```

Expected: build fails because `FilesController` does not yet accept `IPathSecurityService`.

- [ ] **Step 3: Inject and use path security in `FilesController`**

Modify the using block in `src/backend/Agw.Tasks/Controllers/FilesController.cs`:

```csharp
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Services;
using Agw.Tasks.Application.Files;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
```

Modify fields and constructor:

```csharp
private readonly ILogger<FilesController> _logger;
private readonly IGitCommandService _gitCommandService;
private readonly IPathSecurityService _pathSecurityService;

public FilesController(
    ILogger<FilesController> logger,
    IGitCommandService gitCommandService,
    IPathSecurityService pathSecurityService)
{
    _logger = logger;
    _gitCommandService = gitCommandService;
    _pathSecurityService = pathSecurityService;
}
```

Add this helper inside the controller:

```csharp
private bool TryResolveRequiredPath(string? path, out string normalizedPath, out IActionResult? errorResult)
{
    normalizedPath = string.Empty;
    errorResult = null;

    if (string.IsNullOrWhiteSpace(path))
    {
        errorResult = BadRequest(new { error = "Path parameter is required" });
        return false;
    }

    if (!_pathSecurityService.TryResolvePath(path, out normalizedPath))
    {
        errorResult = BadRequest(new { error = "Invalid path" });
        return false;
    }

    return true;
}
```

Replace each repeated block:

```csharp
if (string.IsNullOrEmpty(path))
{
    return BadRequest(new { error = "Path parameter is required" });
}

var normalizedPath = Path.GetFullPath(path);
if (normalizedPath.Contains(".."))
{
    return BadRequest(new { error = "Invalid path" });
}
```

with:

```csharp
if (!TryResolveRequiredPath(path, out var normalizedPath, out var errorResult))
{
    return errorResult!;
}
```

- [ ] **Step 4: Remove `Task.Run` from search**

Keep the public action name and async-shaped return, but make the search body synchronous:

```csharp
public Task<IActionResult> SearchAsync(
    [FromQuery] string? path,
    [FromQuery] string? keyword,
    [FromQuery] int limit = 10,
    [FromQuery] bool recursive = true)
{
    if (!TryResolveRequiredPath(path, out var normalizedPath, out var errorResult))
    {
        return Task.FromResult(errorResult!);
    }

    keyword ??= "";

    try
    {
        if (!Directory.Exists(normalizedPath))
        {
            return Task.FromResult<IActionResult>(NotFound(new { error = "Directory not found" }));
        }

        var results = new List<FileSearchResult>();
        if (recursive)
        {
            SearchFilesRecursive(normalizedPath, normalizedPath, keyword, limit, results);
        }
        else
        {
            SearchFilesNonRecursive(normalizedPath, keyword, limit, results);
        }

        results = results
            .OrderBy(x => x.Type == "file")
            .ThenBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();

        return Task.FromResult<IActionResult>(Ok(new FileSearchResponse { Results = results }));
    }
    catch (UnauthorizedAccessException ex)
    {
        _logger.LogError(ex, "Access denied searching directory: {Path}", normalizedPath);
        return Task.FromResult<IActionResult>(StatusCode(403, new { error = "Access denied", details = ex.Message }));
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error searching directory: {Path}", normalizedPath);
        return Task.FromResult<IActionResult>(StatusCode(500, new { error = "Failed to search directory", details = ex.Message }));
    }
}
```

- [ ] **Step 5: Run controller tests to verify GREEN**

Run:

```bash
dotnet test tests/Agw.Tasks.Tests/Agw.Tasks.Tests.csproj --filter FilesControllerPathSecurityTests
```

Expected: all `FilesControllerPathSecurityTests` pass.

---

### Task 4: Exception and Runtime Base Cleanup

**Files:**
- Move: `src/backend/Agw.Agents/Application/AgentRun/RuntimServiceBase.cs` to `src/backend/Agw.Agents/Application/AgentRun/RuntimeServiceBase.cs`
- Modify: `src/backend/Agw.Agents/Application/AgentRun/AgentRuntimeService.cs`
- Modify: `src/backend/Agw.Agents/Application/Agentflows/AgentflowRuntimeService.cs`
- Modify: `src/backend/Agw.A2A/AgwA2ARequestHandler.cs`
- Modify: `src/backend/Agw.Tools/Impl/Files/LsTool.cs`
- Modify: `src/backend/Agw.Tools/Impl/Files/ReadFileTool.cs`
- Modify: `src/backend/Agw.Integrations/Tools/GitHub/GitHubTools.cs`

- [ ] **Step 1: Rename the base class**

Move `RuntimServiceBase.cs` to `RuntimeServiceBase.cs` and change the class declaration:

```csharp
namespace Agw.Agents.Application.AgentRun;

public class RuntimeServiceBase
{
    protected static AgwMessage CreateTurnFinishedMessage(CancellationToken cancellationToken)
    {
        var content = new AgwTextContent
        {
            Content = "",
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                { "type", "turn-finished" },
                { "status", "" }
            }
        };

        var payload = new AgwMessage(
            Guid.NewGuid().Normalize(),
            "$agw-server",
            AiRole.System,
            new List<AgwContent> { content });
        return payload;
    }
}
```

Update base type references:

```csharp
public class AgentRuntimeService : RuntimeServiceBase, IAgentRuntimeService
```

```csharp
public class AgentflowRuntimeService : RuntimeServiceBase
```

- [ ] **Step 2: Replace generic exceptions**

Apply these exact replacements:

```csharp
throw new InvalidOperationException("AI agent could not be created for execution.");
```

```csharp
throw new A2AException(
    $"No agent handler configured for agent '{agentName}'.",
    A2AErrorCode.InvalidAgentResponse);
```

```csharp
throw new ArgumentException("Directory is required.", nameof(toolParams));
```

```csharp
throw new DirectoryNotFoundException($"Directory '{toolParams.Directory}' does not exist.");
```

```csharp
throw new ArgumentException("File path is required.", nameof(readFileParam));
```

```csharp
throw new FileNotFoundException($"File '{readFileParam.FilePath}' does not exist.", readFileParam.FilePath);
```

```csharp
throw new InvalidOperationException("GitHub OAuth token was not found.");
```

- [ ] **Step 3: Build to verify mechanical cleanup**

Run:

```bash
dotnet build Agw.slnx
```

Expected: build succeeds with no errors from the rename or exception replacements.

---

### Task 5: Final Verification

**Files:**
- No new files.

- [ ] **Step 1: Run targeted backend tests**

Run:

```bash
dotnet test tests/Agw.Tasks.Tests/Agw.Tasks.Tests.csproj --filter "PathSecurityServiceTests|FilesControllerPathSecurityTests"
```

Expected: targeted tests pass.

- [ ] **Step 2: Run full backend test suite**

Run:

```bash
dotnet test Agw.slnx
```

Expected: solution tests pass.

- [ ] **Step 3: Inspect remaining audit patterns**

Run:

```bash
rg -n "throw new Exception|Contains\\(\"\\.\\.\"\\)|Task.Run\\(\\(\\) =>" src/backend/Agw.Tasks src/backend/Agw.Agents src/backend/Agw.A2A src/backend/Agw.Tools src/backend/Agw.Integrations -g *.cs
```

Expected: no matches for the audit-pointed generic exceptions, no `Contains("..")` path checks in `FilesController`, and no `Task.Run(() =>` wrapper in `FilesController.SearchAsync`.

- [ ] **Step 4: Check git diff**

Run:

```bash
git diff -- src/backend/Agw.Tasks src/backend/Agw.Agents src/backend/Agw.A2A src/backend/Agw.Tools src/backend/Agw.Integrations tests/Agw.Tasks.Tests
```

Expected: diff is limited to the files listed in this plan. Existing unrelated local changes remain untouched.
