# AgentSelector Design

## Goal

Extract the agent and agentflow selector from the Chat page into a reusable business component. Consumers provide the current project and selection, and receive the selected numeric agent type and agent ID.

## Component API

Create `src/clients/web/src/components/agent-selector.tsx` with this public contract:

```ts
export type AgentSelection = {
  agentType: 0 | 1;
  agentId: string;
};

export type AgentSelectorProps = {
  id: string;
  projectId?: string | null;
  value?: AgentSelection | null;
  onSelect: (selection: AgentSelection) => void;
};
```

`agentType` uses the existing execution contract: `0` represents an Agent and `1` represents an Agentflow.

## Responsibilities

`AgentSelector` will:

- Load Agents and Agentflows through React Query using the existing API helpers and cache keys.
- Reuse `buildChatTargetOptions`, `getTargetValue`, and `parseTargetValue` so project restrictions, enabled Agentflows, labels, and sorting stay consistent with Chat.
- Render the existing `SearchableSelect` with Agent and Agentflow groups.
- Convert the controlled `AgentSelection` value to the selector's encoded target value.
- Convert a selected target value back to `{ agentType, agentId }` before calling `onSelect`.
- Display loading and query error states through `SearchableSelect`.

The component will not own Chat session state, URL state, project selection, execution cancellation, or persistence.

## Chat Integration

Replace the target `SearchableSelect` block in the Chat page with `AgentSelector`. The Chat page will continue to own its existing queries and target-option state because those values are also used for route hydration, default selection, execution, suggestions, and error reporting.

The component's queries use the same React Query keys, so the shared cache prevents duplicate network data ownership while keeping `AgentSelector` independently reusable.

When `AgentSelector.onSelect` fires, Chat will encode the returned selection into its existing target value and call the existing target-change handler. This preserves execution detachment, command reset, and settings persistence.

## Error Handling

- A missing project ID does not prevent listing normal Agent and Agentflow options, matching the existing target-option builder behavior.
- Invalid encoded values are ignored and do not invoke `onSelect`.
- Agent or Agentflow query failures are shown inside the selector; Chat retains its existing page-level dependency error message.

## Verification

- Add a component contract test before implementation and confirm it fails because `AgentSelector` does not exist.
- Verify the component owns the Agent and Agentflow queries, preserves grouping, and returns numeric type plus ID.
- Verify the Chat page imports and renders `AgentSelector` instead of the target `SearchableSelect` block.
- Run the focused tests, frontend lint, format check, and production build. Report unrelated baseline failures without modifying them.
