# Agent Skill Auto-Approval and Python Runner Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let definition agents automatically approve all `AgentSkillsProvider` tools and safely execute file-based Python skill scripts.

**Architecture:** Add a focused `PythonSkillScriptRunner` adapter that validates MAF file-skill paths and arguments before invoking Python without a shell. Pass that adapter to `AgentSkillsProvider`, then wrap only skill-enabled definition agents with `ToolApprovalAgent` configured with `AgentSkillsProvider.AllToolsAutoApprovalRule`.

**Tech Stack:** .NET 10, Microsoft.Agents.AI 1.12, `System.Diagnostics.Process`, xUnit v3.

## Global Constraints

- Follow `AGENTS.md` and `docs/rules.md`.
- Do not use C# primary constructors.
- Use `AgwException` and existing `ErrorCodes` for expected application failures.
- Do not auto-approve tools outside `AgentSkillsProvider`.
- Do not modify the database, uploaded skill files, generated artifacts, or unrelated dirty files.
- Do not stage or commit; the user has not authorized Git writes.

---

### Task 1: Constrained Python Skill Script Runner

**Files:**
- Create: `src/server/Agw.Agents/Execution/Agents/Skills/PythonSkillScriptRunner.cs`
- Create: `tests/Agw.Agents.Tests/PythonSkillScriptRunnerTests.cs`

**Interfaces:**
- Consumes: `AgentFileSkill`, `AgentFileSkillScript`, `JsonElement?`, and `CancellationToken` from Microsoft Agent Framework.
- Produces: `PythonSkillScriptRunner.RunAsync(AgentFileSkill, AgentFileSkillScript, JsonElement?, IServiceProvider?, CancellationToken)` compatible with `AgentFileSkillScriptRunner`.
- Produces: an internal path-based overload used by focused tests.

- [ ] **Step 1: Write failing runner tests**

Create `PythonSkillScriptRunnerTests.cs` with tests that exercise the wished-for internal overload:

```csharp
using System.Text.Json;

using Agw.Agents.Execution.Agents.Skills;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Tests;

public class PythonSkillScriptRunnerTests
{
    [Fact]
    public async Task RunAsync_ValidPythonScript_PassesArgumentsAndUsesSkillDirectory()
    {
        var root = CreateTempDirectory();
        var script = Path.Combine(root, "inspect.py");
        await File.WriteAllTextAsync(
            script,
            "import json, os, sys; print(json.dumps({'cwd': os.getcwd(), 'args': sys.argv[1:]}))");
        using var arguments = JsonDocument.Parse("""["alpha beta","literal;value"]""");

        try
        {
            var result = Assert.IsType<string>(await PythonSkillScriptRunner.RunAsync(
                root,
                script,
                arguments.RootElement,
                TestContext.Current.CancellationToken));
            using var output = JsonDocument.Parse(result);

            Assert.Equal(Path.GetFullPath(root), output.RootElement.GetProperty("cwd").GetString());
            Assert.Equal(
                ["alpha beta", "literal;value"],
                output.RootElement.GetProperty("args").EnumerateArray().Select(item => item.GetString()).ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_ScriptOutsideSkillDirectory_ThrowsCommandExecutionFailure()
    {
        var root = CreateTempDirectory();
        var outside = Path.Combine(Path.GetTempPath(), $"agw-outside-{Guid.NewGuid():N}.py");
        await File.WriteAllTextAsync(outside, "print('outside')");

        try
        {
            var exception = await Assert.ThrowsAsync<AgwException>(() =>
                PythonSkillScriptRunner.RunAsync(
                    root,
                    outside,
                    arguments: null,
                    TestContext.Current.CancellationToken));

            Assert.Equal(ErrorCodes.CommandExecutionFailed.Code, exception.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task RunAsync_UnsupportedExtension_ThrowsCommandExecutionFailure()
    {
        var root = CreateTempDirectory();
        var script = Path.Combine(root, "script.sh");
        await File.WriteAllTextAsync(script, "echo unsupported");

        try
        {
            var exception = await Assert.ThrowsAsync<AgwException>(() =>
                PythonSkillScriptRunner.RunAsync(
                    root,
                    script,
                    arguments: null,
                    TestContext.Current.CancellationToken));

            Assert.Equal(ErrorCodes.CommandExecutionFailed.Code, exception.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_NonZeroExit_ThrowsCommandExecutionFailure()
    {
        var root = CreateTempDirectory();
        var script = Path.Combine(root, "fail.py");
        await File.WriteAllTextAsync(script, "raise SystemExit(7)");

        try
        {
            var exception = await Assert.ThrowsAsync<AgwException>(() =>
                PythonSkillScriptRunner.RunAsync(
                    root,
                    script,
                    arguments: null,
                    TestContext.Current.CancellationToken));

            Assert.Equal(ErrorCodes.CommandExecutionFailed.Code, exception.Code);
            Assert.Contains("7", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Timeout_ThrowsCommandTimeout()
    {
        var root = CreateTempDirectory();
        var script = Path.Combine(root, "wait.py");
        await File.WriteAllTextAsync(script, "import time; time.sleep(10)");

        try
        {
            var exception = await Assert.ThrowsAsync<AgwException>(() =>
                PythonSkillScriptRunner.RunAsync(
                    root,
                    script,
                    arguments: null,
                    TestContext.Current.CancellationToken,
                    timeout: TimeSpan.FromMilliseconds(100)));

            Assert.Equal(ErrorCodes.CommandTimeout.Code, exception.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agw-python-skill-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```bash
dotnet test tests/Agw.Agents.Tests --filter "FullyQualifiedName~PythonSkillScriptRunnerTests"
```

Expected: compilation fails because `PythonSkillScriptRunner` does not exist.

- [ ] **Step 3: Implement the minimal runner**

Create `PythonSkillScriptRunner.cs` with this structure:

```csharp
using System.Diagnostics;
using System.Text.Json;

using Agw.Shared.Exceptions;

using Microsoft.Agents.AI;

namespace Agw.Agents.Execution.Agents.Skills;

internal static class PythonSkillScriptRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    public static Task<object?> RunAsync(
        AgentFileSkill skill,
        AgentFileSkillScript script,
        JsonElement? arguments,
        IServiceProvider? serviceProvider,
        CancellationToken cancellationToken)
    {
        return RunAsync(skill.Path, script.FullPath, arguments, cancellationToken);
    }

    internal static async Task<object?> RunAsync(
        string skillPath,
        string scriptPath,
        JsonElement? arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var skillRoot = Path.GetFullPath(skillPath);
        var fullScriptPath = Path.GetFullPath(scriptPath);
        ValidateScriptPath(skillRoot, fullScriptPath);
        var scriptArguments = ParseArguments(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "python" : "python3",
            WorkingDirectory = skillRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(fullScriptPath);
        foreach (var argument in scriptArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new AgwException(ErrorCodes.CommandExecutionFailed, "Failed to start the Python skill script.");
            }
        }
        catch (AgwException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AgwException(
                ErrorCodes.CommandExecutionFailed,
                $"Failed to start Python skill script '{Path.GetFileName(fullScriptPath)}': {ex.Message}",
                ex);
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        using var timeoutSource = new CancellationTokenSource(timeout ?? DefaultTimeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            KillProcess(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw new AgwException(
                ErrorCodes.CommandTimeout,
                $"Python skill script '{Path.GetFileName(fullScriptPath)}' timed out.");
        }
        catch (OperationCanceledException)
        {
            KillProcess(process);
            throw;
        }

        var standardOutput = await standardOutputTask.ConfigureAwait(false);
        var standardError = await standardErrorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new AgwException(
                ErrorCodes.CommandExecutionFailed,
                $"Python skill script '{Path.GetFileName(fullScriptPath)}' exited with code {process.ExitCode}: {standardError.Trim()}");
        }

        return standardOutput.TrimEnd();
    }

    private static void ValidateScriptPath(string skillRoot, string scriptPath)
    {
        if (!Directory.Exists(skillRoot) || !File.Exists(scriptPath))
        {
            throw new AgwException(ErrorCodes.CommandExecutionFailed, "The Python skill script path is invalid.");
        }

        var relativePath = Path.GetRelativePath(skillRoot, scriptPath);
        if (Path.IsPathRooted(relativePath)
            || relativePath.Equals("..", StringComparison.Ordinal)
            || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || !Path.GetExtension(scriptPath).Equals(".py", StringComparison.OrdinalIgnoreCase))
        {
            throw new AgwException(
                ErrorCodes.CommandExecutionFailed,
                "Only Python scripts inside the owning skill directory can be executed.");
        }
    }

    private static IReadOnlyList<string> ParseArguments(JsonElement? arguments)
    {
        if (!arguments.HasValue || arguments.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (arguments.Value.ValueKind != JsonValueKind.Array)
        {
            throw new AgwException(ErrorCodes.CommandExecutionFailed, "Skill script arguments must be a string array.");
        }

        var result = new List<string>();
        foreach (var item in arguments.Value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new AgwException(ErrorCodes.CommandExecutionFailed, "Skill script arguments must contain only strings.");
            }

            result.Add(item.GetString()!);
        }

        return result;
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort: the process may exit between the check and kill.
        }
    }
}
```

- [ ] **Step 4: Run the focused tests and verify GREEN**

Run:

```bash
dotnet test tests/Agw.Agents.Tests --filter "FullyQualifiedName~PythonSkillScriptRunnerTests"
```

Expected: all five runner tests pass.

- [ ] **Step 5: Inspect the task diff without staging or committing**

Run:

```bash
git diff --check -- src/server/Agw.Agents/Execution/Agents/Skills/PythonSkillScriptRunner.cs tests/Agw.Agents.Tests/PythonSkillScriptRunnerTests.cs
```

Expected: exit code 0 and no output.

---

### Task 2: Wire Runner and Skill-Only Auto-Approval

**Files:**
- Modify: `src/server/Agw.Agents/Execution/Agents/AgentRuntimeService.Skills.cs`
- Modify: `src/server/Agw.Agents/Execution/Agents/AgentRuntimeService.CreateDefinitionAgents.cs`
- Modify: `tests/Agw.Agents.Tests/AgentRuntimeServiceSystemCompositionTests.cs`

**Interfaces:**
- Consumes: `PythonSkillScriptRunner.RunAsync` from Task 1.
- Produces: an `AgentSkillsProvider` with executable file scripts.
- Produces: a `ToolApprovalAgent` configured with `AgentSkillsProvider.AllToolsAutoApprovalRule` only when skills are present.

- [ ] **Step 1: Extend the composition test and verify RED**

In `CreateAiAgentAsync_SystemAgent_ComposesProjectCapabilitiesAndPassesEffectiveEnvironmentToMcp`, add these assertions after the existing skills-provider assertions:

```csharp
var approvalAgent = FindInObjectGraph<ToolApprovalAgent>(aiAgent!);
var rulesField = typeof(ToolApprovalAgent).GetField(
    "_autoApprovalRules",
    BindingFlags.Instance | BindingFlags.NonPublic);
Assert.NotNull(rulesField);
var rules = Assert.IsAssignableFrom<IEnumerable<Func<FunctionCallContent, ValueTask<bool>>>>(
    rulesField.GetValue(approvalAgent));
Assert.Contains(AgentSkillsProvider.AllToolsAutoApprovalRule, rules);
Assert.DoesNotContain(ToolApprovalAgent.AllToolsAutoApprovalRule, rules);
```

Run:

```bash
dotnet test tests/Agw.Agents.Tests --filter "FullyQualifiedName~AgentRuntimeServiceSystemCompositionTests.CreateAiAgentAsync_SystemAgent_ComposesProjectCapabilitiesAndPassesEffectiveEnvironmentToMcp"
```

Expected: failure stating that `ToolApprovalAgent` could not be found in the agent object graph.

- [ ] **Step 2: Pass the Python runner into the skills provider**

Add the namespace import and change the provider construction in `AgentRuntimeService.Skills.cs`:

```csharp
using Agw.Agents.Execution.Agents.Skills;

// Existing discovery logic remains unchanged.
return new AgentSkillsProvider(
    skillPaths: skillPaths,
    scriptRunner: PythonSkillScriptRunner.RunAsync);
```

- [ ] **Step 3: Add skill-only automatic approval**

In `CreateDefinitionAgentAsync`, after the provider-specific agent is created and before observability/usage middleware is added, insert:

```csharp
if (skillsProvider != null)
{
    aiAgent = aiAgent.AsBuilder()
        .UseToolApproval(new ToolApprovalAgentOptions
        {
            AutoApprovalRules = [AgentSkillsProvider.AllToolsAutoApprovalRule],
        })
        .Build();
}
```

Do not use `ToolApprovalAgent.AllToolsAutoApprovalRule`; it would broaden trust to unrelated tools.

- [ ] **Step 4: Run the focused composition test and verify GREEN**

Run:

```bash
dotnet test tests/Agw.Agents.Tests --filter "FullyQualifiedName~AgentRuntimeServiceSystemCompositionTests.CreateAiAgentAsync_SystemAgent_ComposesProjectCapabilitiesAndPassesEffectiveEnvironmentToMcp"
```

Expected: the composition test passes and finds the skill-only rule.

- [ ] **Step 5: Run all Agw.Agents tests**

Run:

```bash
dotnet test tests/Agw.Agents.Tests
```

Expected: all tests pass with zero failures.

- [ ] **Step 6: Inspect the task diff without staging or committing**

Run:

```bash
git diff --check -- src/server/Agw.Agents/Execution/Agents/AgentRuntimeService.Skills.cs src/server/Agw.Agents/Execution/Agents/AgentRuntimeService.CreateDefinitionAgents.cs tests/Agw.Agents.Tests/AgentRuntimeServiceSystemCompositionTests.cs
```

Expected: exit code 0 and no output.

---

### Task 3: Repository Verification

**Files:**
- Verify only; no new files.

**Interfaces:**
- Consumes: Tasks 1 and 2.
- Produces: build and test evidence for handoff.

- [ ] **Step 1: Build the solution**

Run:

```bash
dotnet build Agw.slnx
```

Expected: build succeeds with zero errors.

- [ ] **Step 2: Run the focused backend test project again**

Run:

```bash
dotnet test tests/Agw.Agents.Tests
```

Expected: all tests pass with zero failures.

- [ ] **Step 3: Review only the in-scope diff**

Run:

```bash
git diff --check
git status --short
git diff -- src/server/Agw.Agents/Execution/Agents/AgentRuntimeService.Skills.cs src/server/Agw.Agents/Execution/Agents/AgentRuntimeService.CreateDefinitionAgents.cs src/server/Agw.Agents/Execution/Agents/Skills/PythonSkillScriptRunner.cs tests/Agw.Agents.Tests/PythonSkillScriptRunnerTests.cs tests/Agw.Agents.Tests/AgentRuntimeServiceSystemCompositionTests.cs docs/superpowers/specs/2026-07-14-agent-skill-autoapproval-runner-design.md docs/superpowers/plans/2026-07-14-agent-skill-autoapproval-runner.md
```

Expected: no whitespace errors; only the approved implementation, tests, and uncommitted planning documents appear in the reviewed diff. Existing unrelated changes remain untouched.
