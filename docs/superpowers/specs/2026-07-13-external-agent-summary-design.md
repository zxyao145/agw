# Independent Agent Summary Model Design

## Goal

Allow Definition Agents of every type to generate a turn Summary with a model provider selected independently from the Agent's execution model.

## Behavior

- Persist the optional Summary provider in `Agent.SummaryModelProviderId`.
- System Agents still require `ModelProviderId`. When `SummaryModelProviderId` is absent, Summary uses the System Agent's `ModelProviderId` as its default.
- External Agents keep `ModelProviderId` optional. When Summary is enabled, they must explicitly select `SummaryModelProviderId`; their execution provider is never used as an implicit Summary default.
- Definition Agent execution appends a text-only Markdown result after a successful turn when Summary is enabled and an effective Summary provider exists.
- Agentflow behavior is unchanged: only an Output node generates a workflow Summary.

## Scope

The change covers Agent persistence and API contracts, Definition Agent validation/runtime behavior, and Agent create/edit UI. It adds the nullable column to the existing, unapplied `AddAgentSummaries` migration and does not apply the migration.

## Verification

- Domain tests cover System fallback and the External explicit-provider requirement.
- Runtime tests prove the Summary service receives the independent provider and System fallback.
- API and frontend tests cover field round-tripping, dialog state, payloads, and validation.
