# Project Memory

## Architecture

- Always keep persisted entities, domain data objects, value objects, and state snapshots anemic. Put behavior that a rich model would own in a framework-free `Domain/Behaviors/<Entity>Behavior` class, construct it manually around the complete data boundary, and never register it with IoC. Application code supplies external facts, invokes the Behavior, and persists the resulting data.
- Do not create empty Behavior classes for simple CRUD. A Behavior exists only when the corresponding data has real business invariants or state transitions.
- Construct pure Domain Policy classes manually by default. A genuine DomainService that spans multiple data boundaries may be managed by IoC, but it must remain stateless and may depend only on pure Domain components; a single-root DomainService is misplaced entity Behavior.
- Never let a Behavior reference or construct a Policy or DomainService. Application may construct Behavior first for root-local preconditions, separately evaluates Policy into a data-only Decision, then asks Behavior to apply it; dependency direction matters, not a rigid invocation order.
- For an EF-tracked consistency boundary, load the complete root and owned navigations before Behavior mutation, then reconcile children in place by owned key. Never replace a tracked navigation before querying its old rows or delete/re-add duplicate tracked keys.
- Apply selective DDD only to the Agentflow graph subdomain: `AgentflowDefinitionPolicy` produces a data-only Decision, `AgentflowBehavior` applies it to Agentflow plus owned Nodes/Edges, and `AgentflowTopology` serves shared algorithms; simple modules remain ordinary Application + DbContext flows.

## API Design

- For every External Agent, use the canonical AgentName as `AuthorName` on each `AgentResponseUpdate` and persisted `ChatMessage`; never duplicate AgentName in `AdditionalProperties["agentName"]`.
- Store the normalized model identifier only in `AdditionalProperties["modelName"]` on every External Agent update and persisted message; never overload `AuthorName` with a model, and use `""` when neither runtime events nor explicit configuration provide a model.
- Keep External Agent metadata identical across streaming, non-streaming, and history-persistence paths so conversion or replay never changes `AuthorName` or drops `modelName`.

## Tooling

- Never create or merge a pull request automatically. Each remote PR action requires an explicit user request for that action.
- When developing Agw against an adjacent SDK repository, use a local `ProjectReference` instead of publishing or upgrading a temporary NuGet package.
