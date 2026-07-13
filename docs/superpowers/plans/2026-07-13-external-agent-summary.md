# Independent Agent Summary Model Implementation Plan

**Goal:** Give every Definition Agent an optional, independent `SummaryModelProviderId`, with System Agent fallback to its execution model.

## Constraints

- System Agents require `ModelProviderId`; `SummaryModelProviderId` is optional and defaults to it at runtime.
- External Agents require an explicit `SummaryModelProviderId` only when Summary is enabled.
- Agentflow Summary remains Output-node-only.
- Update the existing `AddAgentSummaries` migration without applying it.
- Do not create a Git commit without explicit authorization.

## Tasks

- [x] Add domain and runtime tests for independent provider selection and System fallback.
- [x] Add `SummaryModelProviderId` to the Agent entity, API contracts, application validation, and migration snapshot.
- [x] Resolve and pass the effective Summary provider in both Definition Agent execution paths.
- [x] Add independent Summary provider state, selection, payload mapping, and validation to Agent dialogs.
- [x] Refresh OpenAPI artifacts and focused frontend tests.
- [x] Run the complete scoped backend/frontend verification and inspect the final patch.
