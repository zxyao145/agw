# Agent App Relation Design

## Summary

Allow an `Agent` to associate with multiple persisted integration `AppInstance` records through a new `agent_app_relation` table.
When a system agent is materialized in `AgentRuntimeService`, the runtime must load the related `AppDefinition` records for those app instances, merge their `ToolNames`, deduplicate by tool name, and inject the resulting `AITool` set into the created `AIAgent`.

This change also extends the existing Agent management APIs and frontend page so users can configure these associations from the current create and edit dialogs through a searchable multi-select field labeled `App`.

## Current Context

- `src/backend/Agw.Agents/Domain/Entities/Agent.cs` currently supports:
  - direct JSON tool selection through `Tools`
  - `AgentSkillRelation`
  - `AgentMcpServerRelation`
- `src/backend/Agw.Integrations/Domain/Entities/AppInstance.cs` persists OAuth client configuration and authorization state, but nothing in the Agent domain currently references it.
- `src/backend/Agw.Agents/Application/Agents/AgentRuntimeService.cs` currently builds runtime tools from:
  - `agent.Tools`
  - MCP tool servers linked through `AgentMcpServerRelation`
- `AppDefinition` is a static catalog entry loaded from `IntegrationConstants.AppList` and already exposes `ToolNames`.
- `src/backend/Agw.Agents/Controllers/Manager/AgentsController.cs` and its request contracts currently support:
  - base agent fields
  - `skillIds`
  - `mcpToolServerIds`
- `src/frontend/web/src/app/(app)/(agents)/agents/page.tsx` already manages multi-selection for skills, direct tools, and MCP tool servers, but does not load or render integration app instances.

## Requirements

### Functional Requirements

1. An `Agent` can be associated with multiple `AppInstance` records.
2. An `AppInstance` can be associated with multiple `Agent` records.
3. Agent create and update APIs must accept the chosen app instance ids.
4. Agent list and get APIs must return the persisted app relations so the frontend can edit them later.
5. When `CreateDefinitionAgent` builds a system `AIAgent`, it must:
   - inspect the agent's related `AppInstance` records
   - resolve each instance's `AppDefinition`
   - read `AppDefinition.ToolNames`
   - create corresponding `AITool` objects from `_toolRegistry`
   - merge them with existing agent tools
6. Tool injection must deduplicate by tool name for tools resolved from named sources.

### UX Requirements

1. The Agent create and edit dialogs must add a new field labeled `App`.
2. `App` must be a searchable multi-select, not a plain single `Select`.
3. Each option must clearly identify the target app instance using:
   - app display name
   - client id
   - provider
   - authorization state
4. Existing associated apps must be shown as selected when editing an agent.

### Data and Safety Requirements

- Missing or stale app relations must not prevent loading an agent edit form.
- Missing `AppDefinition` entries at runtime must not fail agent creation; they should be skipped with a warning.
- Tool names collected from direct agent tools and app-derived tools must not create duplicate `AITool` entries in the final runtime set.

## Proposed Design

## Data Model

Add a new relation entity in `src/backend/Agw.Agents/Domain/Entities/AgentAppRelation.cs`.

Fields:

- `AgentId`
- `AppInstanceId`
- navigation to `Agent`
- navigation to `AppInstance`

Database shape:

- table name: `agent_app_relation`
- composite primary key: `{ AgentId, AppInstanceId }`
- index on `AppInstanceId`
- cascade delete from both `Agent` and `AppInstance`

Entity updates:

- `Agent` gains `ICollection<AgentAppRelation> AgentAppRelations`
- `AppInstance` gains `ICollection<AgentAppRelation> AgentAppRelations`
- `AgwDbContext` gains `DbSet<AgentAppRelation> AgentAppRelations`

## Backend API Changes

## Agent Contracts

Extend:

- `AgentCreateRequest`
- `AgentUpdateRequest`

with:

- `List<Guid>? AppInstanceIds`

Field naming stays backend-oriented as `AppInstanceIds`.
Only the frontend label becomes `App`.

## Agent CRUD Service

Extend `AgentRuntimeService` create and update methods to accept `appInstanceIds` alongside the existing:

- `mcpToolServerIds`
- `skillIds`

Add a new synchronization method:

- `SyncAgentAppRelationsAsync(Guid agentId, IEnumerable<Guid>? appInstanceIds)`

Behavior:

1. remove existing `AgentAppRelation` rows for the agent
2. normalize requested ids by:
   - filtering empty ids
   - deduplicating
3. load existing `AppInstance` rows for those ids
4. create relation rows only for ids that actually exist

## Agent Read Paths

Update:

- `ListAgentsAsync`
- `GetAgentAsync`

to include `AgentAppRelations` so the current controller behavior, which returns domain entities directly, exposes the relation set to the frontend.

This keeps the existing API style intact for this iteration.

## Runtime Tool Resolution

## Source Order

Runtime tools continue to come from three sources:

1. direct agent tool names from `Agent.Tools`
2. app-derived tool names from `AgentAppRelation -> AppInstance -> AppDefinition.ToolNames`
3. MCP tools from linked MCP servers

## App Tool Expansion

`CreateAgentTools` should be extended to:

1. load the agent's app relations
2. load the linked `AppInstance` records
3. group by `AppName`
4. resolve matching `AppDefinition` entries from the app definition repository
5. flatten all `ToolNames`
6. merge those names with direct agent tool names
7. deduplicate by tool name using case-insensitive comparison
8. call `_toolRegistry.CreateAIFunctions(...)` once for the deduplicated set

Behavioral rules:

- if an `AppInstance` points to an unknown `AppName`, skip it and log a warning
- if an `AppDefinition` has no `ToolNames`, it contributes nothing
- if the same tool name appears multiple times across multiple app instances or overlaps with `Agent.Tools`, inject it only once
- MCP tools remain appended as actual tool instances after name-based tool creation; if later the system needs deduplication across MCP and local tools, that can be handled separately, but it is out of scope for this change

## Dependency Updates

`AgentRuntimeService` will need access to:

- `IRepository<AgentAppRelation>`
- `IRepository<AppInstance>`
- `IRepository<AppDefinition>`

These should be injected through the existing constructor and stored as new private fields.

## Frontend Design

## Data Queries

The Agents page should add a new query:

- `GET /api/integrations/app-instances`

This query runs alongside the existing queries for:

- agents
- model providers
- skills
- tools
- MCP tool servers

## Agent Form Field

Add a new field labeled `App` to the shared `AgentFormFields` component.

The field should use a searchable multi-select interaction:

- trigger button opens a popover or dropdown-style panel
- search input filters options by display name, client id, provider, and subject when present
- checkbox-style selection allows multiple app instances
- selected items render as badges below the control

Preferred option display:

- primary text: `DisplayName`
- secondary text: `ClientId`
- supporting metadata:
  - provider
  - authorization state such as `Authorized`, `Expired`, or `Not authorized`

## Form Behavior

Create dialog:

- store selected values in `selectedAppInstanceIds`
- submit them as `appInstanceIds`

Edit dialog:

- initialize selected values from `agent.agentAppRelations`
- allow changing selections with the same control
- submit them as `appInstanceIds`

Field label:

- the visible label is exactly `App`

## Empty State

If no app instances exist:

- keep the `App` selector present but disabled
- show helper text pointing users to the integrations page to create a connection first

## Frontend Type Updates

Update frontend types so the page can model:

- `AgentAppRelation`
- `AppInstance` list items needed by the selector
- `appInstanceIds` in create and update requests

Because the agents page already uses local frontend types around backend responses, this can be introduced without first refactoring the entire page to generated OpenAPI types.

## Testing Strategy

## Backend Tests

Add focused tests for:

### DbContext

- `AgentAppRelation` composite key prevents duplicate `(AgentId, AppInstanceId)` pairs
- deleting an `AppInstance` removes related `AgentAppRelation` rows

### Agent CRUD

- create agent persists `AgentAppRelation` rows from `AppInstanceIds`
- update agent replaces previous app relations
- list agent returns `AgentAppRelations`
- get agent returns `AgentAppRelations`

### Runtime

- app-linked tool names are injected into the created agent definition runtime
- overlapping tool names from multiple apps are deduplicated
- unknown app definitions are ignored without failing runtime creation

Use test-first changes around `AgentRuntimeService` so the new app-derived tool source is proven through failing tests before implementation.

## Frontend Verification

Add lightweight tests for:

- the agents page queries integration app instances
- the shared form renders an `App` field
- the `App` selector filters options from a search term
- selected app relations are submitted in create and edit requests
- edit mode rehydrates selected app ids from `agent.agentAppRelations`

Run focused verification for the changed route and backend tests rather than claiming repo-wide success from unrelated areas.

## Scope Boundaries

This change does not include:

- editing `AppInstance` details from the Agent page
- creating new app instances inline inside the Agent dialog
- instance-specific tool renaming or tool namespacing
- cross-deduplication between local `AITool` instances and MCP-discovered tool instances
- a broader DTO refactor of the Agent management API
