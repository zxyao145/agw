# Agent Skill Auto-Approval and Python Runner Design

## Goal

Allow definition agents to load, read, and execute their assigned file-based skills without stopping on Microsoft Agent Framework tool-approval requests, while keeping approval scoped to skill tools and executing Python scripts through a constrained host runner.

## Scope

- Apply to definition agents that receive an `AgentSkillsProvider`.
- Automatically approve only `load_skill`, `read_skill_resource`, and `run_skill_script` by using `AgentSkillsProvider.AllToolsAutoApprovalRule`.
- Do not automatically approve unrelated approval-required tools.
- Support Python skill scripts (`.py`) for the current `xhs-explore` use case.
- Do not change skill records, agent-skill relations, external agents, or frontend approval UI.

## Runtime Composition

`AgentRuntimeService` will construct `AgentSkillsProvider` with an `AgentFileSkillScriptRunner`. A definition agent that has a skills provider will then be wrapped with `ToolApprovalAgent` through the MAF builder API and configured with `AgentSkillsProvider.AllToolsAutoApprovalRule`.

The wrapper must be added only when a skills provider exists. This keeps agents without skills unchanged and prevents the approval middleware from implying a broader approval policy.

## Python Script Runner

The runner receives the owning file skill, the discovered script, raw JSON arguments, the optional service provider, and a cancellation token. It will:

1. Resolve and validate the skill root and script paths.
2. Reject scripts outside the skill root and scripts whose extension is not `.py`.
3. Use `python3` on macOS/Linux and `python` on Windows.
4. Set the working directory to the skill root so relative resources behave as authored.
5. pass each JSON array element as one command-line argument without shell interpolation.
6. Enforce a fixed execution timeout and honor caller cancellation.
7. Capture standard output, standard error, and the exit code.
8. Return standard output on success and throw an `AgwException` with an existing command-execution error code on failure or timeout.

No shell will be involved, which prevents shell metacharacters in model-provided arguments from being interpreted as commands.

## Data Flow

1. The model sees `xhs-explore` in the injected skills catalog.
2. The model calls `load_skill`.
3. `ToolApprovalAgent` matches the skill-only auto-approval rule and immediately continues the inner agent run.
4. The model calls `run_skill_script` with the discovered script name and JSON argument array.
5. The same rule approves the call.
6. `AgentFileSkillScriptRunner` validates and invokes the Python script.
7. Script output is returned to the model as the tool result.

## Error Handling

- Invalid argument JSON, unsupported extensions, and path escapes are rejected before process creation.
- A missing Python executable, non-zero exit code, timeout, or process-start failure becomes an `AgwException` using an existing error code and a contextual message.
- Standard error may be included in the application error message, but no secrets or environment dump will be logged.
- Cancellation terminates the child process and propagates cancellation.

## Tests

- A composition test proves agents with skills use skill-only automatic approval.
- A runner test proves a Python script receives distinct arguments and returns stdout.
- Runner tests prove unsupported extensions and paths outside the skill root are rejected.
- A runner test proves a non-zero process exit is reported as an application error.
- The focused `Agw.Agents.Tests` project and the solution build verify integration.

## Security Boundary

Automatic approval is intentionally limited to tools created by `AgentSkillsProvider`; `ToolApprovalAgent.AllToolsAutoApprovalRule` must not be used. Script execution remains constrained by the runner even though `run_skill_script` is trusted for approval purposes.
