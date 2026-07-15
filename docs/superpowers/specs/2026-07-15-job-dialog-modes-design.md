# Job Dialog Modes Design

## Goal

Simplify the Create Job and Edit Job dialogs by showing only the scheduling fields by default, while allowing users to reveal optional job settings through an Advanced toggle. Replace the separate Agent Type and Agent ID controls with the reusable `AgentSelector`.

## Default Mode

Both Create and Edit dialogs open in the default mode every time. The form displays:

- Project ID
- Agent ID through `AgentSelector`
- Trigger Type
- Trigger Value, Interval, or Run Time according to the trigger type
- Prompt

The existing two-column desktop layout and single-column responsive layout remain. Prompt spans both desktop columns.

The footer order is:

1. Cancel
2. Create Job or Save Changes
3. Advanced

The Advanced button uses the outline style and toggles to Basic while the additional fields are visible. Closing and reopening either dialog resets this display state to the default mode; the existing Create/Edit form reset behavior remains unchanged.

## Advanced Mode

Advanced mode adds these existing fields to the form:

- Job Name
- Max Retry Count
- Enabled

The fields retain their current defaults: blank name, maximum retry count `3`, and enabled `true` for new jobs. Edit loads the persisted values and preserves them even when Advanced remains collapsed.

The existing Edit Status control is removed from the dialog. Edit requests continue to send the previously loaded status unchanged.

## AgentSelector Extension

Extend `AgentSelector` without changing its existing Chat behavior:

- Add optional `clearable`, `placeholder`, and `onClear` props.
- Keep the existing `onSelect({ agentType, agentId })` callback for Agent and Agentflow selections.
- Jobs passes `clearable`, uses `Not assigned` as the placeholder, and clears both `agentType` and `agentId` through `onClear`.
- Chat keeps the current defaults and remains non-clearable.

Jobs no longer loads or builds its own Agent and Agentflow options for the dialog. `AgentSelector` uses the shared React Query cache and the selected Project ID for the existing project-specific filtering rules.

## Request and Server Behavior

The web client allows an empty Job Name and sends it as an empty string. Existing validation for Project ID, trigger values, agent type, retry count, and Edit status remains.

`JobAppService` trims a provided name. When the Create or Update request name is blank, it generates:

```text
job-{job count + 1}-{yyyyMMdd}
```

The job count comes from the Job repository before saving, and the date comes from `TimeProvider.GetUtcNow()` formatted as an invariant UTC `yyyyMMdd` value. The count-based number may be reused after deletions or during concurrent creates; this is accepted because Job Name is not unique.

## Error Handling

- Query failures inside `AgentSelector` continue to use `getApiErrorMessage` and render in the selector.
- Invalid retry counts and trigger values continue to be rejected by the existing client validation.
- Server-generated names satisfy the required, maximum-200-character Job entity property.
- Existing names are unchanged unless the submitted Create or Update name is blank.

## Verification

- Add failing frontend contract tests for the default/advanced field split, AgentSelector usage, footer toggle, and removal of the client-side required-name check.
- Add failing AgentSelector tests for clearable mode and `onClear` support.
- Add failing backend tests for a supplied name and a generated Count + 1 UTC name.
- Implement only enough frontend and backend behavior to pass the tests.
- Run focused frontend and Jobs backend tests, frontend lint and format checks, backend build/test checks, and the frontend production build.
- Verify the dialog in a browser when an authenticated local session is available; otherwise report the authentication boundary.
