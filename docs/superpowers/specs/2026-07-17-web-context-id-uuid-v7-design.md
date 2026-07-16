# Web Identifier UUID v7 Design

## Goal

Generate new Web chat Context IDs and user message IDs as UUID version 7 values using the `uuid` package.

## Scope

- Add `uuid@^11.1.1` as a direct Web dependency.
- Replace the `id128` UUID v4 Context ID generator with `uuid.v7()`.
- Replace the `id128` ULID user message ID generator with `uuid.v7()`.
- Remove the now-unused `id128` dependency.
- Leave Mermaid's transient `crypto.randomUUID()` render ID unchanged.
- Do not modify the Mobile client or backend.

## Design

Add a small Web UUID utility that exports `createUuidV7(): string`. The utility delegates directly to `uuid.v7()` and is the single Web boundary for generating persisted client identifiers. The shared Chat component will use this function when no Context ID already exists, and the execution-stream helper will use it when creating a user message.

Existing Context IDs and message IDs remain unchanged. Only newly created identifiers use UUID v7. UUIDs continue to use the canonical hyphenated string representation expected by the backend and the `(ProjectId, ContextId)` unique index.

## Error Handling

No fallback generator will be added. If the platform cannot provide the secure random source required by `uuid`, generation should fail instead of silently producing a weaker identifier. Supported Web browsers already provide the required Web Crypto API.

## Testing and Verification

- Add a focused unit test that calls `createUuidV7()` and verifies the value is valid and reports version 7 through the `uuid` package.
- Update the execution-stream test harness to replace the UUID utility import with a deterministic test generator, then verify new user messages use its result.
- Run the focused test before and after implementation to demonstrate the red-green cycle.
- Run the Web test suite, lint, formatting check, and production build.
- Confirm `id128`, `Uuid4`, and `Ulid` are absent from Web production source and dependency metadata.
- Confirm the only remaining `crypto.randomUUID()` use is the unchanged Mermaid render ID.
