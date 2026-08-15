# Agw.Tools

`Agw.Tools` owns the built-in Tool catalog and the runtime materialization of
Tools and Tool Blocks.

## Capability model

```text
Tool Capability
├── Tool       independently selectable
└── ToolBlock  atomic group of member Tools
```

A Tool exposes one callable operation to the model and can be added or removed
independently. A ToolBlock represents a coherent group of Tools whose behavior
and state must stay consistent. Its member Tools are therefore selected,
materialized, and removed as one unit.

Current Tool Blocks:

- `todo`: `todos_add`, `todos_list`, `todos_complete`, and related Todo tools.
- `mode`: `mode_get` and `mode_set`.
- `project-memory`: Agw project-memory tools backed by the database or
  `<Project.Workspace>/.agw/memory`. The same project shares memory across
  agents and conversations; filesystem-backed memory is shared when Projects
  point to the same Workspace.
- `user-memory`: database-only Markdown memory bound to the authenticated user.
  It follows that user across Agents, Projects, and conversations without being
  visible to other users.
- `file-access`: Harness file-access tools scoped to `Project.Workspace`.
- `background-agents`: one-level background delegation tools.

Context-aware standalone Tools:

- `web_search`: uses the provider-hosted marker when supported and otherwise
  materializes Agw local search with a `tool-warning`.
- `run_shell`: materializes the configured local or Docker shell executor and
  its context provider. It remains independently selectable.

The existing `bash` and `powershell` Tools remain ordinary standalone Tools.

## Data and selection

Agent and Project definitions use one strongly typed `tools` field:

```json
[
  {
    "kind": "tool",
    "definition": {
      "name": "web_search",
      "options": {}
    }
  },
  {
    "kind": "toolBlock",
    "definition": {
      "name": "project-memory",
      "options": {
        "storage": "database"
      }
    }
  },
  {
    "kind": "toolBlock",
    "definition": {
      "name": "user-memory",
      "options": {}
    }
  }
]
```

- The outer `ToolValueObject` is polymorphic on `kind`: `tool` or
  `toolBlock`.
- The nested `definition` is polymorphic on `name` and resolves to a concrete
  `ToolDefinition` or `ToolBlockDefinition`.
- `options` is always required and is always a JSON object; parameterless
  definitions use `{}`.
- Agent and Project values are merged by `definition.name`; a Project
  definition replaces the Agent definition with the same name.
- `null` and `[]` both mean that the current layer adds nothing.
- `background-agents` is Agent-only.
- New Agents and Projects start with an empty `tools` list.
- Legacy database values such as `["web_search"]` are read as typed Tool
  values and are written back in the new shape on the next save. Unknown
  legacy names fail instead of being ignored.

ToolBlock member names never appear as selectable catalog items. Selecting a
member such as `todos_add` as a standalone Tool fails with an error directing the
caller to its owning ToolBlock.

## Catalog

`GET /api/tools` is the single catalog interface. It returns both kinds:

```json
{
  "kind": "toolBlock",
  "name": "todo",
  "displayName": "Todo",
  "memberToolNames": ["todos_add", "todos_list", "todos_complete"],
  "scopes": 3,
  "requiresWorkspace": false
}
```

`kind` is either `tool` or `toolBlock`. `/api/tools/by-category` and
`/api/tools/{name}` use the same shape. There is no separate ToolBlock endpoint.

## Runtime architecture

```text
Agent + Project definitions
          |
          v
AgentCapabilityComposer
  |-- ToolRegistryService -------- ordinary and contextual Tools
  |-- ToolValueResolution -------- Agent/Project merge by definition.name
  |-- ToolBlockRegistry ---------- atomic Tool Blocks
  |-- Connections / MCP / Skills
          |
          v
ToolContribution
  |-- Tools
  |-- ContextProviders
  |-- LoopEvaluators
  |-- AutoApprovalRules
  |-- Warnings
  `-- owned IAsyncDisposable resources
          |
          v
AsAgwAgent
```

`ToolContribution` is the common runtime result for both standalone contextual
Tools and ToolBlocks. The composer flattens its Tools for model invocation and
passes the remaining providers, evaluators, approval rules, and warnings into
the Agent pipeline.

An aggregate contribution owns its child contributions. Resources are released
in last-in, first-out order when the Agent capability lease is disposed. If
materialization fails, already-created contributions are still disposed.

Name validation occurs at catalog construction and runtime composition:

- duplicate Tool names fail;
- duplicate ToolBlock names fail;
- duplicate member ownership fails;
- a ToolBlock name cannot collide with a Tool or member name;
- Tools contributed by Connections, MCP servers, contextual Tools, and
  ToolBlocks cannot collide.

## Core types

- `IAgwTool`: ordinary built-in Tool implementation.
- `IContextualTool`: standalone Tool that needs Agent, Project, workspace,
  provider, or environment context before it can be created.
- `IToolBlock`: atomic Tool group implementation.
- `ToolValueObject`: outer `kind`-discriminated persisted value.
- `ToolDefinition`: `name`-discriminated standalone Tool configuration.
- `ToolBlockDefinition`: `name`-discriminated ToolBlock configuration.
- `ToolBlockDescriptor`: catalog metadata and member names.
- `ToolValueResolution`: Agent/Project merge rules.
- `ToolMaterializationContext`: runtime Agent/Project context.
- `ToolContribution`: materialized runtime behavior and owned resources.
- `ToolRegistryService`: unified Tool and ToolBlock catalog plus standalone Tool
  materialization.
- `ToolBlockRegistry`: ToolBlock validation and materialization.

`AgwWorkspaceProvider` is deliberately not represented by any catalog item. It
belongs to `Agw.Agents` and is attached to every System Agent as a core context
provider.

## State and approval

State belongs to the runtime behavior that needs it, not to the catalog:

- Todo and Mode use MAF session-backed providers.
- Project Memory uses an Agw-owned provider with no Agent-session state.
- Project Memory database storage is isolated by Project ID.
- Project Memory filesystem storage is rooted below
  `<Project.Workspace>/.agw/memory`; Projects using the same Workspace share it.
- User Memory is always stored in the database and isolated by the authenticated
  user ID. Only its Markdown content is encrypted; names and descriptions stay
  searchable.
- The User Memory context provider contributes at most 50 names and complete
  Markdown bodies. Descriptions are display metadata for management UI and
  `user_memory_list`; they are not injected into model context.
- Background relations and results are persisted by their owning runtime.

### Memory scope and privacy

Use User Memory for personal preferences and context that should follow one user
across projects. Use Project Memory for knowledge owned by a project and shared
by every Agent or user working in that project. User Memory never uses the
filesystem, while Project Memory may use either database or workspace storage.
User Memory automatically injects up to 50 complete bodies. Additional entries
remain available through `user_memory_list` and `user_memory_read`.

Tool Approval is an Agent pipeline concern. A Tool or ToolBlock contributes
auto-approval rules where appropriate, while `AsAgwAgent` applies the approval
middleware consistently. File writes and shell execution remain approval
gated.

Tool runtime messages use author `tools` and these message types:

- `tool-todo-snapshot`
- `tool-mode-status`
- `tool-background-task-status`
- `tool-warning`

## Extending the module

### Add a standalone Tool

Use `IAgwTool` or the existing Tool attributes when the Tool can be created
without Agent/Project context. Add its concrete `ToolDefinition`, stable
`JsonDerivedType` name mapping, and execution implementation together. Startup
validation enforces this one-to-one relationship.

### Add a contextual Tool

1. Implement `IContextualTool`.
2. Add the concrete `ToolDefinition` and its `JsonDerivedType` mapping.
3. Provide a `ToolInfo` descriptor with `kind = tool`.
4. Materialize a `ToolContribution`.
5. Transfer every executor, provider, or client lifetime to the contribution.
6. Register the implementation in `AddTools()`.

### Add a ToolBlock

1. Add a derived `ToolBlockDefinition` and stable `name` discriminator in
   `Agw.Data`.
2. Add the matching runtime name to `ToolBlockNames`.
3. Implement `IToolBlock` in its own folder under `ToolBlocks/Blocks`.
4. List every member Tool name in `ToolBlockDescriptor.MemberToolNames`.
5. Materialize all members and their state providers in one
   `ToolContribution`.
6. Register it in `AddTools()`.
7. Add registry, resolution, materialization, API, and UI tests.

Do not register ToolBlock members independently and do not add implicit
selection defaults.

## Verification

```bash
dotnet test tests/Agw.Tools.Tests/Agw.Tools.Tests.csproj
dotnet test tests/Agw.Agents.Tests/Agw.Agents.Tests.csproj
dotnet build Agw.slnx

cd src/clients
pnpm exec turbo run build --filter=@agw/tools --filter=@agw/agents --filter=@agw/projects
pnpm test:boundaries
```
