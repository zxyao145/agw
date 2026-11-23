import assert from "node:assert/strict";
import test from "node:test";

import { createEmptyDocument, createNode } from "./agentflow-document";
import { createAgentflowEditorStore, selectCanRedo, selectCanUndo } from "./agentflow-editor-store";

function createStore() {
  const input = createEmptyDocument().nodes[0];
  const agent = createNode("agent", { x: 200, y: 0 });
  return createAgentflowEditorStore({
    document: { ...createEmptyDocument(), nodes: [input, agent] },
    agentflowId: null,
    baseRevision: 0,
    publishedRevision: 0,
  });
}

test("editor store tracks dirty state and supports undo and redo", () => {
  const store = createStore();
  store.getState().updateDocument((document) => ({ ...document, name: "Changed" }));

  assert.equal(store.getState().isDirty, true);
  assert.equal(selectCanUndo(store.getState()), true);
  store.getState().undo();
  assert.equal(store.getState().document.name, "");
  assert.equal(store.getState().isDirty, false);
  assert.equal(selectCanRedo(store.getState()), true);
});

test("saving records the new base revision", () => {
  const store = createStore();
  store.getState().updateDocument((document) => ({ ...document, name: "Saved" }));
  store.getState().markSaved({ agentflowId: "flow-1", latestRevision: 1 });

  assert.equal(store.getState().agentflowId, "flow-1");
  assert.equal(store.getState().baseRevision, 1);
  assert.equal(store.getState().isDirty, false);
});

test("selecting a node clears edge and member selection", () => {
  const store = createStore();
  const agent = store.getState().document.nodes[1];
  const team = {
    ...createNode("magenticBlock", { x: 400, y: 0 }),
    members: [
      {
        memberId: "member-1",
        kind: "agent" as const,
        relateId: "agent-1",
        name: "Member",
        instructions: null,
        isManager: true,
      },
    ],
  };
  const edge = {
    edgeId: "edge-1",
    sourceNodeId: "input",
    targetNodeId: agent.nodeId,
    isFanInBarrier: false,
    isSwitchDefault: false,
    switchCaseOrder: 0,
    choiceId: null,
    label: null,
    conditions: [],
  };
  store.getState().updateDocument((document) => ({
    ...document,
    nodes: [...document.nodes, team],
    edges: [edge],
  }));
  store.getState().selectEdge("edge-1");
  store.getState().selectMember({ nodeId: team.nodeId, memberId: "member-1" });
  store.getState().selectNodes([agent.nodeId]);

  assert.deepEqual(store.getState().selectedNodeIds, [agent.nodeId]);
  assert.equal(store.getState().selectedEdgeId, null);
  assert.equal(store.getState().selectedMember, null);
});

test("a selection of removed nodes, edges or members is cleared after edits", () => {
  const store = createStore();
  const node = store.getState().document.nodes[1];
  const edge = {
    edgeId: "edge-1",
    sourceNodeId: "input",
    targetNodeId: node.nodeId,
    isFanInBarrier: false,
    isSwitchDefault: false,
    switchCaseOrder: 0,
    choiceId: null,
    label: null,
    conditions: [],
  };
  store.getState().updateDocument((document) => ({ ...document, edges: [edge] }));
  store.getState().selectNodes([node.nodeId]);
  store
    .getState()
    .updateDocument((document) => ({ ...document, nodes: document.nodes.slice(0, 1), edges: [] }));

  assert.deepEqual(store.getState().selectedNodeIds, []);
  assert.equal(store.getState().selectedEdgeId, null);
  assert.equal(store.getState().selectedMember, null);
});

test("undo restores a multi-node move in one step", () => {
  const store = createStore();
  const before = store.getState().document.nodes.map((node) => node.position);
  store.getState().updateDocument((document) => ({
    ...document,
    nodes: document.nodes.map((node) => ({
      ...node,
      position: { x: Number(node.position.x) + 40, y: Number(node.position.y) + 20 },
    })),
  }));

  assert.equal(store.getState().past.length, 1);
  store.getState().undo();
  assert.deepEqual(
    store.getState().document.nodes.map((node) => node.position),
    before,
  );
});

test("grouped edits create one history entry", () => {
  const store = createStore();
  store.getState().updateDocument((document) => ({ ...document, name: "F" }), { group: "name" });
  store.getState().updateDocument((document) => ({ ...document, name: "Flow" }), { group: "name" });
  store.getState().commitHistoryGroup();

  assert.equal(store.getState().past.length, 1);
  store.getState().undo();
  assert.equal(store.getState().document.name, "");
});

test("saving freezes edits while a request is active", () => {
  const store = createStore();
  store.getState().setSaving(true);
  store.getState().updateDocument((document) => ({ ...document, name: "Ignored" }));

  assert.equal(store.getState().document.name, "");
  store.getState().setSaving(false);
  assert.equal(selectCanRedo(store.getState()), false);
});
