# Block Participant Visibility Fix

## Problem

Block auto-layout rewrites participant positions independently from the root canvas block node. Adding another participant still derives its position from the block node, so the new participant can be created far outside the current block viewport.

## Design

- Derive a new participant's initial position from the current block participant bounds when members already exist.
- Keep the existing block-relative position only for the first participant.
- After the new node appears in the block canvas, focus the ReactFlow viewport on that node without remounting the canvas or resetting unrelated state.
- Keep membership data, save payloads, and root-canvas behavior unchanged.

## Testing

- Add a pure regression test proving that a distant root block coordinate does not control the next member position after existing participants have been laid out near the origin.
- Run the focused block-membership tests, frontend lint, changed-file formatting checks, and the production build.
