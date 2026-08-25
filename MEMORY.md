# Project Memory

## Architecture

- Always keep persisted entities, domain data objects, value objects, and state snapshots anemic. Put behavior that a rich model would own in a framework-free `Domain/Behaviors/<Entity>Behavior` class, construct it manually around the complete data boundary, and never register it with IoC. Application code supplies external facts, invokes the Behavior, and persists the resulting data.
- Do not create empty Behavior classes for simple CRUD. A Behavior exists only when the corresponding data has real business invariants or state transitions.
- Construct pure Domain Policy classes manually by default. A genuine DomainService that spans multiple data boundaries may be managed by IoC, but it must remain stateless and may depend only on pure Domain components; a single-root DomainService is misplaced entity Behavior.
