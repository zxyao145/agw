# Job Enabled Switch Design

## Goal

Add an `Enabled` column to the Jobs table so a user can enable or disable one Job without opening the edit dialog or overwriting unrelated scheduling fields.

## API

Add `PUT /api/jobs/enabled` with a request body containing `jobId` and `isEnabled`. The application service updates only `IsEnabled`, `UpdateBy`, and `UpdateTime`, then returns the updated Job. Enabling a Job wakes the scheduler prefetch loop so an eligible pending Job is considered promptly.

The endpoint returns the standard Bens.Results envelope. A missing Job uses the existing resource-not-found error mapping.

## Jobs Table

Add an `Enabled` column before `Status`. Each row renders the shared `Switch` component.

The interaction is pessimistic:

- Clicking a Switch starts a request without changing its checked value.
- Only that Job's Switch is disabled while its request is pending.
- After the Server succeeds, update the cached Jobs row with the returned Job.
- On failure, keep the existing value and show an error toast.

Separate Jobs may be toggled concurrently, while repeated requests for the same Job are prevented until its current request completes.

## Tests

- Application service: only enabled state and audit fields change, missing Jobs return null, and enabling wakes the scheduler.
- API: the dedicated route uses the Bens.Results envelope and updates the Job.
- Web: the table contains the Enabled column and Switch, uses per-Job pending state, and updates the cache only in the success callback.
- Regenerate the Web OpenAPI types after the backend contract changes.

## Out of Scope

- Resetting a completed, paused, or failed Job to pending.
- Changing Job schedules, retry counters, or status when toggling enabled state.
- Bulk enable or disable actions.
