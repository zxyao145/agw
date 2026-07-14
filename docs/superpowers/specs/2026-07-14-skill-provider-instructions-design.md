# Skill Provider Instructions Design

## Goal

Ensure definition agents use MAF Skill tools for Skill content instead of treating Skill-relative paths as project-workspace paths.

Persisted Skills remain stored below `AgwDataPaths.SkillsDirectory`. A project workspace continues to contain ordinary project files and is not a fallback location for Skill scripts or resources.

## Current Behavior and Root Cause

`AgentRuntimeService.CreateSkillsProviderAsync` already resolves each related Skill from its persisted `ContentPath` under `AgwDataPaths.Root`. For uploaded Skills, this resolves to `AgwDataPaths.SkillsDirectory/{skillName}`. The resulting absolute paths are passed to `AgentSkillsProvider`, so `load_skill` and `run_skill_script` do not depend on the project workspace.

The failure occurs after `load_skill`: a Skill body may describe a relative command such as `python scripts/cli.py`. The model can interpret that path relative to the project workspace and call ordinary bash or file tools. When the Skill is not copied into that workspace, those tools report that the path does not exist even though the MAF Skill provider loaded the Skill successfully.

## Selected Approach

Configure `AgentSkillsProviderOptions.SkillsInstructionPrompt` when constructing `AgentSkillsProvider`.

The custom template must contain MAF's required `{skills}` placeholder and must provide the complete Skill usage contract because it replaces, rather than appends to, the MAF default template.

The template will instruct the model to:

- use `load_skill` for complete Skill instructions;
- treat Skill files as external to the project workspace;
- never use bash, glob, directory-listing, or project file tools to locate Skill files;
- use `read_skill_resource` for Skill resources;
- use `run_skill_script` for Skill scripts;
- pass the exact Skill and script names advertised by the provider; and
- report Skill-tool not-found errors without searching the project workspace.

This configuration is scoped to agents that have an `AgentSkillsProvider`. It does not change ordinary workspace behavior or the behavior of bash and file tools.

## Runtime Flow

1. Agw loads the Agent and Project Skill relations from the database.
2. Each persisted Skill path is resolved under `AgwDataPaths.Root`; the normal uploaded-Skill path is `AgwDataPaths.SkillsDirectory/{skillName}`.
3. `AgentSkillsProvider` discovers the Skill and replaces `{skills}` in the configured instruction template with the advertised Skill list.
4. The model calls `load_skill` with the advertised Skill name.
5. When execution is needed, the model calls `run_skill_script` with the advertised script name and a JSON array of string arguments.
6. MAF resolves the selected `AgentFileSkill` and `AgentFileSkillScript`; `LocalSkillScriptRunner` executes the absolute script path with the Skill directory as its working directory.

## Errors

- If none of the related persisted Skill directories exist, Agw retains its existing warning and does not attach an `AgentSkillsProvider`.
- If `load_skill`, `read_skill_resource`, or `run_skill_script` cannot find the requested item, the tool error is returned to the model.
- The model must report that error and must not retry by searching the project workspace.
- No new API error code or database change is required.

## Testing

Focused Agent tests will verify that:

- the provider still receives absolute paths resolved from `AgwDataPaths`;
- the configured Skill instruction template contains `{skills}` and the three MAF Skill tool names;
- the template explicitly separates Skill paths from the project workspace and prohibits ordinary workspace tools for Skill lookup; and
- the existing script runner, extension allowlist, and automatic approval configuration remain unchanged.

Repository verification will run `Agw.Agents.Tests`, build `Agw.slnx`, and run the full solution test suite.

## Non-Goals

- Do not copy, link, or materialize Skills inside project workspaces.
- Do not add path fallback behavior to bash, glob, directory-listing, or project file tools.
- Do not rewrite uploaded `SKILL.md` files.
- Do not change Skill persistence, database schema, script language support, script approval, or runner process behavior.
