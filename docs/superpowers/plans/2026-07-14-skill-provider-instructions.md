# Skill Provider Instructions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ensure definition agents use MAF Skill tools for Skill content and never resolve Skill-relative paths through the project workspace.

**Architecture:** Keep persisted Skill discovery and absolute path resolution unchanged. Configure `AgentSkillsProviderOptions.SkillsInstructionPrompt` on the existing `AgentSkillsProvider` so the model uses `load_skill`, `read_skill_resource`, and `run_skill_script`; verify the provider configuration through the existing runtime composition test.

**Tech Stack:** .NET 10, Microsoft.Agents.AI 1.12, xUnit v3.

## Global Constraints

- Follow `AGENTS.md`, `docs/rules.md`, and `docs/superpowers/specs/2026-07-14-skill-provider-instructions-design.md`.
- Keep persisted Skills below `AgwDataPaths.SkillsDirectory`; do not copy or link them into project workspaces.
- Do not add Skill path fallback to bash, glob, directory-listing, or project file tools.
- Do not rewrite uploaded `SKILL.md` files.
- Do not change Skill persistence, database schema, script language support, automatic approval, or runner process behavior.
- Preserve unrelated staged and unstaged worktree changes.
- Do not stage or commit; the user has not authorized Git writes.

---

## File Structure

- `src/server/Agw.Agents/Execution/Agents/AgentRuntimeService.Skills.cs`: owns persisted Skill path resolution and construction of the MAF `AgentSkillsProvider`; add the custom instruction template and pass it through provider options.
- `tests/Agw.Agents.Tests/AgentRuntimeServiceSystemCompositionTests.cs`: verifies the fully composed definition agent contains the expected Skill paths, script allowlist, approval rules, and new provider instructions.
- `docs/superpowers/specs/2026-07-14-skill-provider-instructions-design.md`: approved behavior and non-goals; no implementation edit is expected.

---

### Task 1: Configure the Skill Provider Instructions

**Files:**
- Modify: `tests/Agw.Agents.Tests/AgentRuntimeServiceSystemCompositionTests.cs:255-274`
- Modify: `src/server/Agw.Agents/Execution/Agents/AgentRuntimeService.Skills.cs:9-40`

**Interfaces:**
- Consumes: `AgentSkillsProviderOptions.SkillsInstructionPrompt`, the required literal `{skills}` placeholder, `AgentSkillsProvider.LoadSkillToolName`, `AgentSkillsProvider.ReadSkillResourceToolName`, and `AgentSkillsProvider.RunSkillScriptToolName` from Microsoft.Agents.AI 1.12.
- Produces: a configured `AgentSkillsProvider` whose prompt advertises the discovered Skills and prohibits workspace-based Skill lookup.

- [ ] **Step 1: Add a failing runtime composition assertion**

In `CreateAiAgentAsync_SystemAgent_ComposesProjectCapabilitiesAndPassesEffectiveEnvironmentToMcp`, immediately after the existing absolute Skill path assertions, add:

```csharp
var skillsInstructionPrompt = Assert.Single(
    providerStrings,
    value => value.Contains(
        "Skill files are stored outside the project workspace.",
        StringComparison.Ordinal));
Assert.Contains("{skills}", skillsInstructionPrompt, StringComparison.Ordinal);
Assert.Contains(AgentSkillsProvider.LoadSkillToolName, skillsInstructionPrompt, StringComparison.Ordinal);
Assert.Contains(AgentSkillsProvider.ReadSkillResourceToolName, skillsInstructionPrompt, StringComparison.Ordinal);
Assert.Contains(AgentSkillsProvider.RunSkillScriptToolName, skillsInstructionPrompt, StringComparison.Ordinal);
Assert.Contains(
    "Never use bash, glob, ls, or project file tools to locate skill files.",
    skillsInstructionPrompt,
    StringComparison.Ordinal);
Assert.Contains(
    "Do not search the project workspace.",
    skillsInstructionPrompt,
    StringComparison.Ordinal);
```

Keep the existing path, extension allowlist, and approval assertions in the same test.

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```bash
dotnet test tests/Agw.Agents.Tests --no-restore \
  --filter "FullyQualifiedName~AgentRuntimeServiceSystemCompositionTests.CreateAiAgentAsync_SystemAgent_ComposesProjectCapabilitiesAndPassesEffectiveEnvironmentToMcp"
```

Expected: FAIL at `Assert.Single` because the current provider object graph does not contain the Agw-specific sentence `Skill files are stored outside the project workspace.`

- [ ] **Step 3: Add the complete Skill instruction template**

In `AgentRuntimeService.Skills.cs`, add this constant inside `AgentRuntimeService`, before `CreateSkillsProviderAsync`:

```csharp
private const string SkillsInstructionPrompt =
    """
    # Skills

    The following skills are available:

    {skills}

    Skill usage rules:

    - Use `load_skill` to load a skill's complete instructions.
    - Skill files are stored outside the project workspace.
    - Never use bash, glob, ls, or project file tools to locate skill files.
    - Use `read_skill_resource` to read a skill resource.
    - Use `run_skill_script` to execute a skill script.
    - Pass the exact skill and script names advertised by the skill provider.
    - If a skill or script is not found, report the error. Do not search the project workspace.
    """;
```

The literal `{skills}` must remain unescaped and must not use an interpolated raw string. MAF replaces it with the discovered Skill list.

- [ ] **Step 4: Pass the template through provider options**

Replace the existing provider construction with:

```csharp
return new AgentSkillsProvider(
    skillPaths: skillPaths,
    scriptRunner: LocalSkillScriptRunner.RunAsync,
    fileOptions: new AgentFileSkillsSourceOptions
    {
        AllowedScriptExtensions = [.. LocalSkillScriptRunner.SupportedScriptExtensions],
    },
    options: new AgentSkillsProviderOptions
    {
        SkillsInstructionPrompt = SkillsInstructionPrompt,
    });
```

Do not change `GetSkillAbsolutePath`; it already maps the normal persisted `skills/{skillName}` content path under `AgwDataPaths.Root`, which is `AgwDataPaths.SkillsDirectory/{skillName}`.

- [ ] **Step 5: Run the focused test and verify GREEN**

Run:

```bash
dotnet test tests/Agw.Agents.Tests --no-restore \
  --filter "FullyQualifiedName~AgentRuntimeServiceSystemCompositionTests.CreateAiAgentAsync_SystemAgent_ComposesProjectCapabilitiesAndPassesEffectiveEnvironmentToMcp"
```

Expected: PASS. The same test must also continue to prove the absolute Skill paths, `.py`/`.js`/`.cs` allowlist, and Skill-tool approval rule.

- [ ] **Step 6: Run the complete Agent test project**

Run:

```bash
dotnet test tests/Agw.Agents.Tests --no-restore
```

Expected: all `Agw.Agents.Tests` tests pass with zero failures.

- [ ] **Step 7: Review the focused change**

Run:

```bash
git diff --check -- \
  src/server/Agw.Agents/Execution/Agents/AgentRuntimeService.Skills.cs \
  tests/Agw.Agents.Tests/AgentRuntimeServiceSystemCompositionTests.cs

git diff -- \
  src/server/Agw.Agents/Execution/Agents/AgentRuntimeService.Skills.cs \
  tests/Agw.Agents.Tests/AgentRuntimeServiceSystemCompositionTests.cs
```

Expected: no whitespace errors; every changed production or test line implements or verifies the approved Skill-tool instruction contract.

---

### Task 2: Repository Verification

**Files:**
- Verify only; no new implementation files.

**Interfaces:**
- Consumes: the configured provider from Task 1.
- Produces: build, full-suite, and worktree-scope evidence for handoff.

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

- [ ] **Step 3: Verify final whitespace and worktree scope**

Run:

```bash
git diff --check
git status --short
git diff --cached --name-only
git diff -- \
  src/server/Agw.Agents/Execution/Agents/AgentRuntimeService.Skills.cs \
  tests/Agw.Agents.Tests/AgentRuntimeServiceSystemCompositionTests.cs
```

Expected:

- `git diff --check` reports no whitespace errors.
- The focused diff contains only the approved provider prompt and its test assertions.
- Existing unrelated staged and unstaged files remain untouched.
- The design and implementation plan remain uncommitted unless the user separately authorizes Git writes.

- [ ] **Step 4: Confirm excluded areas remain unchanged**

Run:

```bash
git status --short | rg \
  "AgwDataPaths|SkillAppService|SkillDomainService|LocalSkillScriptRunner|Migrations|src/clients" || true
```

Expected: no new changes from this task appear in Skill persistence, runner behavior, migrations, or clients. Pre-existing unrelated changes may still appear and must not be altered.
