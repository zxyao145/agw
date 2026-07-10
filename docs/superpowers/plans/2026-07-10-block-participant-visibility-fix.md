# Block Participant Visibility Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep newly added block participants near the existing block-member canvas and bring the new node into view.

**Architecture:** Add a pure positioning helper beside the existing block-membership helpers so the coordinate rule is independently testable. The builder will use that helper and retain the active ReactFlow instance long enough to focus the newly inserted node after it is rendered.

**Tech Stack:** TypeScript, React 19, ReactFlow 11, Node.js `node:test`.

## Global Constraints

- Preserve existing membership JSON, save payloads, and root-canvas behavior.
- Do not add dependencies or refactor unrelated builder code.
- Do not create a Git commit unless the user explicitly authorizes it.

---

### Task 1: Position and focus newly added block participants

**Files:**
- Modify: `src/frontend/web/src/app/(app)/(agents)/agentflows/components/block-membership.test.ts`
- Modify: `src/frontend/web/src/app/(app)/(agents)/agentflows/components/block-membership.ts`
- Modify: `src/frontend/web/src/app/(app)/(agents)/agentflows/components/visual-agentflow-builder.tsx`

**Interfaces:**
- Consumes: `getBlockParticipantNodes(nodes, blockId)` and ReactFlow's `ReactFlowInstance.fitView(...)`.
- Produces: `getNextBlockParticipantPosition(nodes, blockId): XYPosition`.

- [x] **Step 1: Write the failing regression test**

Add a test with a block at `{ x: 4000, y: 3000 }` and already laid-out members near the origin. Assert that `getNextBlockParticipantPosition` returns a position anchored to the rightmost existing member rather than the distant block:

```ts
test("getNextBlockParticipantPosition stays near laid-out block members", async () => {
  const { getNextBlockParticipantPosition } = await loadBlockMembership();
  const block = {
    ...node("block", NodeKind.ConcurrentBlock, { participantNodeIds: ["french", "spanish"] }),
    position: { x: 4000, y: 3000 },
  };
  const french = { ...node("french", NodeKind.Agent), position: { x: 12, y: 12 } };
  const spanish = { ...node("spanish", NodeKind.Agent), position: { x: 12, y: 80 } };

  assert.deepEqual(getNextBlockParticipantPosition([block, french, spanish], "block"), {
    x: 272,
    y: 12,
  });
});
```

- [x] **Step 2: Run the focused test and verify RED**

Run:

```bash
cd src/frontend/web
node --experimental-strip-types --test 'src/app/(app)/(agents)/agentflows/components/block-membership.test.ts'
```

Expected: FAIL because `getNextBlockParticipantPosition` does not exist.

- [x] **Step 3: Implement the pure positioning helper**

In `block-membership.ts`, import `XYPosition` as a type and add:

```ts
const BLOCK_PARTICIPANT_NODE_WIDTH = 220;
const BLOCK_PARTICIPANT_NODE_GAP = 40;

export function getNextBlockParticipantPosition<TNodeData extends MembershipNodeData>(
  nodes: Node<TNodeData>[],
  blockId: string,
): XYPosition {
  const blockNode = nodes.find((node) => node.id === blockId);
  if (!blockNode || !isBlockNodeKind(blockNode.data.kind)) return { x: 0, y: 0 };

  const participants = getBlockParticipantNodes(nodes, blockId);
  if (participants.length === 0) {
    return { x: blockNode.position.x + 40, y: blockNode.position.y + 136 };
  }

  const anchor = participants.reduce((rightmost, participant) =>
    participant.position.x > rightmost.position.x ? participant : rightmost,
  );
  return {
    x: anchor.position.x + (anchor.width ?? BLOCK_PARTICIPANT_NODE_WIDTH) + BLOCK_PARTICIPANT_NODE_GAP,
    y: anchor.position.y,
  };
}
```

- [x] **Step 4: Use the helper and focus the rendered node**

In `visual-agentflow-builder.tsx`:

1. Import `ReactFlowInstance` and `getNextBlockParticipantPosition`.
2. Store the current ReactFlow instance and a pending focus node id.
3. Replace the block-relative participant position with `getNextBlockParticipantPosition(current, blockId)`.
4. Set the pending focus id after adding the member.
5. After `canvasNodes` contains that id in block scope, schedule `reactFlowInstance.fitView({ nodes: [{ id }], padding: 0.5, maxZoom: 1, duration: 200 })` on the next animation frame. Clear the pending id only when `fitView` returns `true`, allowing node-dimension updates to trigger a retry when measurement is not ready.
6. Pass `setReactFlowInstance` to ReactFlow's `onInit` prop.

- [x] **Step 5: Run the focused test and verify GREEN**

Run the Step 2 command again.

Expected: all block-membership tests pass.

- [x] **Step 6: Verify the frontend**

Run:

```bash
cd src/frontend/web
pnpm lint
pnpm exec oxfmt --check \
  'src/app/(app)/(agents)/agentflows/components/block-membership.test.ts' \
  'src/app/(app)/(agents)/agentflows/components/block-membership.ts' \
  'src/app/(app)/(agents)/agentflows/components/visual-agentflow-builder.tsx'
pnpm build
```

Expected: lint has zero errors, all three changed source files are formatted, and the production build succeeds.
