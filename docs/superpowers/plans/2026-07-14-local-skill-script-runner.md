# Local Skill Script Runner Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the Python-only file skill runner with a safe local runner that supports `.py`, `.js`, and `.cs`, while excluding `.csx` and every other script type from MAF discovery.

**Architecture:** Rename the runner and centralize its supported-extension list. Select a subprocess command from the validated extension, reuse the existing no-shell process boundary and safety behavior, and pass the same extension list into `AgentFileSkillsSourceOptions` so discovery and execution cannot drift.

**Tech Stack:** .NET 10, Microsoft.Agents.AI 1.12, `System.Diagnostics.Process`, xUnit v3.

## Global Constraints

- Follow `AGENTS.md`, `docs/rules.md`, and `docs/superpowers/specs/2026-07-14-local-skill-script-runner-design.md`.
- Support exactly `.py`, `.js`, and `.cs`; do not support `.csx`, `.sh`, or `.ps1`.
- Execute `.cs` as a .NET 10 file-based application; require the .NET 10 SDK and do not infer a `.csproj`.
- Preserve path validation, no-shell argument handling, output capture, cancellation, two-minute timeout, process-tree termination, and existing `AgwException` codes.
- Keep skill-only `ToolApprovalAgent` behavior unchanged.
- Do not modify the database, uploaded skill files, frontend, migrations, or unrelated dirty files.
- Do not stage or commit; the user has not authorized Git writes.

---

### Task 1: Generalize the Script Runner

**Files:**
- Delete: `src/server/Agw.Agents/Execution/Agents/Skills/PythonSkillScriptRunner.cs`
- Create: `src/server/Agw.Agents/Execution/Agents/Skills/LocalSkillScriptRunner.cs`
- Modify: `src/server/Agw.Agents/Execution/Agents/AgentRuntimeService.Skills.cs`
- Delete: `tests/Agw.Agents.Tests/PythonSkillScriptRunnerTests.cs`
- Create: `tests/Agw.Agents.Tests/LocalSkillScriptRunnerTests.cs`

**Interfaces:**
- Consumes: MAF `AgentFileSkillScriptRunner` parameters.
- Produces: `LocalSkillScriptRunner.RunAsync(...)`, `LocalSkillScriptRunner.SupportedScriptExtensions`, and an internal `CreateStartInfo(...)` seam for command-mapping tests.

- [ ] **Step 1: Rename the test class conceptually and add failing language-mapping tests**

Move the existing runner tests into `LocalSkillScriptRunnerTests.cs`, replace every `PythonSkillScriptRunner` reference with `LocalSkillScriptRunner`, and retain the Python execution, path-escape, non-zero-exit, and timeout tests.

Replace the unsupported-extension test with this theory:

```csharp
[Theory]
[InlineData("script.csx")]
[InlineData("script.sh")]
[InlineData("script.ps1")]
public async Task RunAsync_UnsupportedExtension_ThrowsCommandExecutionFailure(string fileName)
{
    var root = CreateTempDirectory();
    var script = Path.Combine(root, fileName);
    await File.WriteAllTextAsync(script, "unsupported", TestContext.Current.CancellationToken);

    try
    {
        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            LocalSkillScriptRunner.RunAsync(
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
```

Add JavaScript and C# command tests that do not require Node.js to be installed on the test host:

```csharp
[Fact]
public void CreateStartInfo_JavaScriptScript_UsesNodeWithLiteralArguments()
{
    var root = CreateTempDirectory();
    var script = Path.Combine(root, "inspect.js");

    try
    {
        var startInfo = LocalSkillScriptRunner.CreateStartInfo(
            root,
            script,
            ["alpha beta", "literal;value"]);

        Assert.Equal("node", startInfo.FileName);
        Assert.Equal([script, "alpha beta", "literal;value"], startInfo.ArgumentList);
        Assert.False(startInfo.UseShellExecute);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

[Fact]
public void CreateStartInfo_CSharpScript_UsesDotnetFileAppWithArgumentSeparator()
{
    var root = CreateTempDirectory();
    var script = Path.Combine(root, "inspect.cs");

    try
    {
        var startInfo = LocalSkillScriptRunner.CreateStartInfo(
            root,
            script,
            ["alpha beta", "literal;value"]);

        Assert.Equal("dotnet", startInfo.FileName);
        Assert.Equal(
            ["run", "--file", script, "--", "alpha beta", "literal;value"],
            startInfo.ArgumentList);
        Assert.False(startInfo.UseShellExecute);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```bash
dotnet test tests/Agw.Agents.Tests --filter "FullyQualifiedName~LocalSkillScriptRunnerTests"
```

Expected: compilation fails because `LocalSkillScriptRunner` does not exist.

- [ ] **Step 3: Implement `LocalSkillScriptRunner`**

Rename the production class and expose one immutable-by-contract extension list:

```csharp
internal static class LocalSkillScriptRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    internal static IReadOnlyList<string> SupportedScriptExtensions { get; } =
        [".py", ".js", ".cs"];
```

Keep both existing `RunAsync` signatures. After validation and JSON argument parsing, replace the Python-specific `ProcessStartInfo` construction with:

```csharp
var startInfo = CreateStartInfo(skillRoot, fullScriptPath, scriptArguments);
```

Implement the command selector as follows:

```csharp
internal static ProcessStartInfo CreateStartInfo(
    string skillRoot,
    string scriptPath,
    IReadOnlyList<string> scriptArguments)
{
    var extension = Path.GetExtension(scriptPath);
    var startInfo = new ProcessStartInfo
    {
        WorkingDirectory = skillRoot,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    if (extension.Equals(".py", StringComparison.OrdinalIgnoreCase))
    {
        startInfo.FileName = OperatingSystem.IsWindows() ? "python" : "python3";
        startInfo.ArgumentList.Add(scriptPath);
    }
    else if (extension.Equals(".js", StringComparison.OrdinalIgnoreCase))
    {
        startInfo.FileName = "node";
        startInfo.ArgumentList.Add(scriptPath);
    }
    else if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
    {
        startInfo.FileName = "dotnet";
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--file");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("--");
    }
    else
    {
        throw new AgwException(
            ErrorCodes.CommandExecutionFailed,
            $"Skill script extension '{extension}' is not supported.");
    }

    foreach (var argument in scriptArguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    return startInfo;
}
```

Change `ValidateScriptPath` to accept only values in `SupportedScriptExtensions` using `StringComparer.OrdinalIgnoreCase`. Generalize Python-specific start, exit, and timeout messages to say “skill script” and include `Path.GetFileName(fullScriptPath)` where useful. Do not change the error codes or process lifecycle.

Update the existing provider's `scriptRunner` reference from `PythonSkillScriptRunner.RunAsync` to `LocalSkillScriptRunner.RunAsync`, but do not add `AgentFileSkillsSourceOptions` yet; Task 2 must first demonstrate the discovery mismatch with a failing test.

- [ ] **Step 4: Run the focused tests and verify GREEN**

Run:

```bash
dotnet test tests/Agw.Agents.Tests --filter "FullyQualifiedName~LocalSkillScriptRunnerTests"
```

Expected: all runner tests pass, including JavaScript and C# mapping and `.csx` rejection.

- [ ] **Step 5: Inspect the runner diff**

Run:

```bash
git diff --check -- src/server/Agw.Agents/Execution/Agents/Skills tests/Agw.Agents.Tests/LocalSkillScriptRunnerTests.cs tests/Agw.Agents.Tests/PythonSkillScriptRunnerTests.cs
```

Expected: exit code 0 and no whitespace errors.

---

### Task 2: Align MAF Discovery With Runner Support

**Files:**
- Modify: `src/server/Agw.Agents/Execution/Agents/AgentRuntimeService.Skills.cs`
- Modify: `tests/Agw.Agents.Tests/AgentRuntimeServiceSystemCompositionTests.cs`

**Interfaces:**
- Consumes: `LocalSkillScriptRunner.RunAsync` and `LocalSkillScriptRunner.SupportedScriptExtensions`.
- Produces: an `AgentSkillsProvider` that discovers only `.py`, `.js`, and `.cs` scripts.

- [ ] **Step 1: Extend the composition test and verify RED**

After the existing skill path assertions, add:

```csharp
Assert.Contains(".py", providerStrings);
Assert.Contains(".js", providerStrings);
Assert.Contains(".cs", providerStrings);
Assert.DoesNotContain(".csx", providerStrings);
Assert.DoesNotContain(".sh", providerStrings);
Assert.DoesNotContain(".ps1", providerStrings);
```

Run:

```bash
dotnet test tests/Agw.Agents.Tests --filter "FullyQualifiedName~AgentRuntimeServiceSystemCompositionTests.CreateAiAgentAsync_SystemAgent_ComposesProjectCapabilitiesAndPassesEffectiveEnvironmentToMcp"
```

Expected: the test fails because the provider still uses MAF's default script-extension set, which contains `.csx`, `.sh`, and `.ps1`.

- [ ] **Step 2: Wire the local runner and matching file options**

Change the provider construction to:

```csharp
return new AgentSkillsProvider(
    skillPaths: skillPaths,
    scriptRunner: LocalSkillScriptRunner.RunAsync,
    fileOptions: new AgentFileSkillsSourceOptions
    {
        AllowedScriptExtensions = [.. LocalSkillScriptRunner.SupportedScriptExtensions],
    });
```

Do not change the `ToolApprovalAgent` wiring in `AgentRuntimeService.CreateDefinitionAgents.cs`.

- [ ] **Step 3: Run the focused composition test and verify GREEN**

Run:

```bash
dotnet test tests/Agw.Agents.Tests --filter "FullyQualifiedName~AgentRuntimeServiceSystemCompositionTests.CreateAiAgentAsync_SystemAgent_ComposesProjectCapabilitiesAndPassesEffectiveEnvironmentToMcp"
```

Expected: the composition test passes and the existing skill-only approval assertions remain green.

- [ ] **Step 4: Run all Agents tests**

Run:

```bash
dotnet test tests/Agw.Agents.Tests
```

Expected: all tests pass with zero failures.

- [ ] **Step 5: Inspect the wiring diff**

Run:

```bash
git diff --check -- src/server/Agw.Agents/Execution/Agents/AgentRuntimeService.Skills.cs tests/Agw.Agents.Tests/AgentRuntimeServiceSystemCompositionTests.cs
```

Expected: exit code 0 and no whitespace errors.

---

### Task 3: Repository Verification

**Files:**
- Verify only; no new files.

**Interfaces:**
- Consumes: Tasks 1 and 2.
- Produces: build, test, and scope evidence for handoff.

- [ ] **Step 1: Build the solution**

Run:

```bash
dotnet build Agw.slnx --no-restore
```

Expected: build succeeds with zero errors.

- [ ] **Step 2: Run the full solution test suite**

Run:

```bash
dotnet test Agw.slnx --no-build --no-restore
```

Expected: every test project passes with zero failures.

- [ ] **Step 3: Review only the in-scope diff**

Run:

```bash
git diff --check
git status --short
git diff -- src/server/Agw.Agents/Execution/Agents/AgentRuntimeService.Skills.cs src/server/Agw.Agents/Execution/Agents/Skills/LocalSkillScriptRunner.cs src/server/Agw.Agents/Execution/Agents/Skills/PythonSkillScriptRunner.cs tests/Agw.Agents.Tests/LocalSkillScriptRunnerTests.cs tests/Agw.Agents.Tests/PythonSkillScriptRunnerTests.cs tests/Agw.Agents.Tests/AgentRuntimeServiceSystemCompositionTests.cs docs/superpowers/specs/2026-07-14-local-skill-script-runner-design.md docs/superpowers/plans/2026-07-14-local-skill-script-runner.md
```

Expected: no whitespace errors; only the approved runner, provider wiring, tests, and uncommitted documentation are part of this task. Existing unrelated staged and unstaged changes remain untouched.
