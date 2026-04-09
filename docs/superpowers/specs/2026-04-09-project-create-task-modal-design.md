# Project Create Task Modal Design

## Summary

Replace the current one-click `Create Task` behavior on the project details page with a modal-based form.
The new flow keeps the current project fixed, requires the user to choose an `Agent` or `Agentflow`, requires a `Prompt`, and still creates a one-time job that starts immediately.

## Current Context

- The project details page currently exposes:
  - a `Details` button that opens a read-only project metadata dialog
  - a `Create Task` button that directly posts a one-time job without additional input
- The chat page already has the desired grouped `Agent / Agentflow` target selector pattern.
- The jobs page already defines the backend job payload contract and the mapping between:
  - `agentType = 0` -> `Agent`
  - `agentType = 1` -> `Agentflow`

## Requirements

### User-Facing Requirements

1. Clicking `Create Task` must open a modal instead of creating a job immediately.
2. The modal must contain a form with exactly these fields:
   - `Project`: read-only, showing the current project information
   - `Agent / Agentflow`: a grouped select, required
   - `Job Name`: editable input with a generated default value
   - `Prompt`: textarea, required
3. The select must expose enough information to derive both:
   - `Agent Type`
   - `Agent ID` or `Agentflow ID`
4. Submitting the form must create a job that:
   - runs once
   - is enabled
   - runs as soon as possible
   - retries zero times

### Fixed Behavior

- `Project` is locked to the current project and cannot be edited in the modal.
- `Agent / Agentflow` is required and must not be preselected automatically.
- `Prompt` is required.
- `Job Name` defaults to `Job-{date}-{random}` and remains editable.

## Proposed UX

### Entry Point

- Keep the existing `Create Task` button in the page header action group.
- Change its behavior from direct mutation to opening a modal.

### Modal Layout

Use a single-column `DialogContent size="lg"` layout.

Field order:

1. `Project`
2. `Agent / Agentflow`
3. `Job Name`
4. `Prompt`

Footer actions:

- `Cancel`
- `Create Task`

### Field Details

#### Project

Display a read-only information block with:

- project name
- project ID
- project status

This should not use editable inputs.

#### Agent / Agentflow

Use the same grouped select structure as the chat page:

- `Agent`
- `Agentflow`

The displayed value format remains user-friendly, but the internal selected value should be encoded as:

- `agent:{id}`
- `agentflow:{id}`

That encoded value is then decoded on submit into:

- `agent:*` -> `agentType = 0`, `agentId = *`
- `agentflow:*` -> `agentType = 1`, `agentId = *`

#### Job Name

Generate a default name when the modal opens:

- format: `Job-YYYYMMDD-HHmmss-rand4`

Important behavior:

- generate once when the modal opens
- do not overwrite user edits afterward

#### Prompt

Use a multiline textarea.

Rules:

- required
- trim before validation
- block submit when empty
- show helper text explaining that prompt content is required for execution

## Data Flow

### Queries

When the project details page is active, it should fetch:

- current project
- current project task history
- agents
- agentflows

The modal itself can consume already-loaded query state from the page.

### Shared Target Helpers

Extract the chat-style target logic into a shared helper module so the project page and chat page use the same encoding and grouping rules.

The shared helper should provide:

- `buildTargetOptions(projectId, agents, agentflows)`
- `getTargetValue(option)`
- `parseTargetValue(value)`

The helper must preserve the current chat behavior, including:

- grouped options for agents and enabled agentflows
- project-aware filtering rules already used by chat

## Submit Payload

On successful validation, the modal must build a job payload with:

- `projectId`: current project ID
- `agentType`: derived from selected target
- `agentId`: derived from selected target
- `name`: trimmed job name
- `prompt`: trimmed prompt
- `triggerType = 1`
- `triggerValue = now + 10s`
- `maxRetryCount = 0`
- `isEnabled = true`

This keeps the backend behavior aligned with the existing quick-run job model while making the execution target explicit.

## Validation Rules

Disable submit when any of the following is true:

- project not loaded
- agents or agentflows still loading
- target not selected
- `jobName.trim()` is empty
- `prompt.trim()` is empty
- create mutation is already pending

Inline behavior:

- show an inline load error in the modal if agent or agentflow data failed to load
- keep the form open on submit failure
- preserve user input on submit failure

## Success and Error Handling

### Success

On successful creation:

- close the modal
- reset form state
- show a success toast
- invalidate:
  - `["jobs"]`
  - `["projects", projectId, "tasks"]`

### Failure

On failed creation:

- keep the modal open
- keep the user-entered values
- show an error toast using the existing API error formatting helper

## File Boundaries

### Modify

- `src/frontend/web/src/app/(app)/(tasks)/projects/[id]/page.tsx`
  - modal open state
  - agent/agentflow queries
  - create-task form state
  - mutation and submit behavior

- `src/frontend/web/src/app/(app)/(tasks)/projects/[id]/project-details.ts`
  - default job name generation
  - project detail item helpers
  - one-time task payload helpers

### Create

- a shared target helper module in frontend shared code
  - reusable by both the project page and chat page

Optional:

- if `page.tsx` becomes too large, extract a `CreateTaskDialog` component in the same route folder

## Testing Strategy

Use the existing lightweight frontend test pattern with `node:test`.

### Logic Tests

Add or update tests for:

- target option encoding and decoding
- mapping from `agent:{id}` / `agentflow:{id}` to `agentType` and `agentId`
- generated default job name shape
- final quick-task job payload generation for:
  - current project
  - selected target
  - job name
  - prompt
  - fixed one-time trigger settings

### Page-Level Checks

Keep page-level tests lightweight:

- verify the page references the modal labels and title constants
- verify `Prompt` is treated as required by the form logic

### Verification

Run:

- focused `node --experimental-strip-types --test ...`
- `pnpm exec oxlint ...`
- `pnpm exec oxfmt --check ...`
- `pnpm exec tsc --noEmit`

If `tsc --noEmit` still reports unrelated pre-existing repository issues, document them separately rather than conflating them with this feature.

## Out of Scope

The modal will not add:

- custom schedule configuration
- retry configuration
- enable/disable toggles
- project switching
- advanced execution settings from chat

Those remain the responsibility of the full jobs management page or other dedicated UI.
