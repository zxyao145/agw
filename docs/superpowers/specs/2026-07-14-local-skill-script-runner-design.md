# Local Skill Script Runner Design

## Goal

Replace the Python-only file skill runner with a local multi-language runner whose discovery and execution capabilities match exactly.

## Supported Languages

The runner supports these file extensions:

| Extension | Runtime command |
| --- | --- |
| `.py` | `python3 <script> <arguments>` on Unix; `python <script> <arguments>` on Windows |
| `.js` | `node <script> <arguments>` |
| `.cs` | `dotnet run --file <script> -- <arguments>` |

The runner does not support `.csx`, `.sh`, or `.ps1`. `AgentFileSkillsSourceOptions.AllowedScriptExtensions` must be set to `.py`, `.js`, and `.cs`, so unsupported files are not advertised as runnable skill scripts.

Python and Node.js remain deployment prerequisites for their respective script types. Running `.cs` scripts additionally requires the .NET 10 SDK because `dotnet run --file` compiles the file-based application at execution time.

## Architecture

Rename `PythonSkillScriptRunner` to `LocalSkillScriptRunner`. The existing `AgentFileSkillScriptRunner` delegate remains the MAF integration boundary.

`LocalSkillScriptRunner` selects a command from the script extension and builds a `ProcessStartInfo` with `UseShellExecute = false`. Script paths and caller arguments are added through `ArgumentList`; no argument is interpreted by a shell. All languages share the existing path validation, working-directory behavior, output capture, cancellation, two-minute timeout, process-tree termination, and `AgwException` error mapping.

`AgentRuntimeService.CreateSkillsProviderAsync` passes both `LocalSkillScriptRunner.RunAsync` and a matching `AgentFileSkillsSourceOptions` allowlist to `AgentSkillsProvider`. Tool approval remains unchanged: only `AgentSkillsProvider` tools are automatically approved.

## C# Semantics

A `.cs` skill script is a .NET 10 file-based application. It may use the file-based app directives supported by the installed .NET SDK. It is not treated as a project, and the runner does not search for or infer a `.csproj`.

`.csx` is explicitly excluded. Full `.csx` semantics require a separate Roslyn scripting engine such as `dotnet-script`, which is not installed and would add another deployment dependency.

## Errors

- A script outside its owning skill directory, a missing file, or an unsupported extension throws `AgwException` with `ErrorCodes.CommandExecutionFailed`.
- A missing language runtime or a non-zero process exit throws `CommandExecutionFailed` and includes useful runtime context without exposing a shell command string.
- A process exceeding the fixed timeout is terminated and throws `ErrorCodes.CommandTimeout`.
- Script arguments must remain a JSON array of strings.

## Tests

Focused tests will verify:

- Python executes end to end with the skill directory as its working directory; JavaScript and C# command mappings use the expected runtimes and literal argument lists.
- Arguments containing spaces and shell metacharacters remain literal values.
- `.csx` and other unsupported extensions are rejected.
- Path escape, non-zero exit, and timeout behavior remain protected.
- The skills provider uses `LocalSkillScriptRunner` and advertises only `.py`, `.js`, and `.cs` scripts.

Repository verification will run `Agw.Agents.Tests`, build `Agw.slnx`, and run the full solution test suite. No database, uploaded skill, migration, frontend, staging, or commit changes are included.
