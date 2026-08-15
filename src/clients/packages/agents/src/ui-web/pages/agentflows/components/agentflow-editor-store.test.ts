import assert from "node:assert/strict";
import test from "node:test";
import type { Edge, Node } from "reactflow";

import { AgentflowEdgeKind, AgentflowNodeKind } from "../../../../types/agentflow.ts";
import {
  createAgentflowEditorStore,
  selectCanRedo,
  selectCanUndo,
  type AgentflowEditorDocument,
  type AgentflowEditorEdgeData,
  type AgentflowEditorNodeData,
} from "./agentflow-editor-store.tsx";

function createNode(id: string, kind: AgentflowNodeKind): Node<AgentflowEditorNodeData> {
  return {
    id,
    type: "dagNode",
    position: { x: 0, y: 0 },
    data: {
      kind,
      title: id,
      relateId: kind === AgentflowNodeKind.Agent ? `runtime-${id}` : null,
      instructions: "",
      configJson: "",
    },
  };
}

function createEdge(id: string, source: string, target: string): Edge<AgentflowEditorEdgeData> {
  return {
    id,
    source,
    target,
    data: {
      kind: AgentflowEdgeKind.Direct,
      label: "",
      conditionJson: "",
      configJson: "",
    },
  };
}

function createDocument(): AgentflowEditorDocument {
  return {
    name: "Initial",
    description: "",
    summaryModelProviderId: "",
    nodes: [
      createNode("input", AgentflowNodeKind.Input),
      createNode("agent", AgentflowNodeKind.Agent),
    ],
    edges: [createEdge("edge", "input", "agent")],
  };
}

function rename(document: AgentflowEditorDocument, name: string): AgentflowEditorDocument {
  return { ...document, name };
}

test("editor store tracks dirty state and supports undo and redo", () => {
  const store = createAgentflowEditorStore(createDocument());
  assert.equal(store.getState().isDirty, false);

  store.getState().updateDocument((document) => rename(document, "Changed"));
  assert.equal(store.getState().isDirty, true);
  assert.equal(selectCanUndo(store.getState()), true);

  store.getState().undo();
  assert.equal(store.getState().document.name, "Initial");
  assert.equal(store.getState().isDirty, false);
  assert.equal(selectCanRedo(store.getState()), true);

  store.getState().redo();
  assert.equal(store.getState().document.name, "Changed");
  assert.equal(store.getState().isDirty, true);
});

test("a new mutation clears redo history", () => {
  const store = createAgentflowEditorStore(createDocument());
  store.getState().updateDocument((document) => rename(document, "First"));
  store.getState().undo();
  store.getState().updateDocument((document) => rename(document, "Second"));

  assert.equal(selectCanRedo(store.getState()), false);
  assert.equal(store.getState().document.name, "Second");
});

test("grouped edits create one history entry", () => {
  const store = createAgentflowEditorStore(createDocument());
  store.getState().updateDocument((document) => rename(document, "C"), { group: "name" });
  store.getState().updateDocument((document) => rename(document, "Ch"), { group: "name" });
  store.getState().updateDocument((document) => rename(document, "Changed"), { group: "name" });
  store.getState().commitHistoryGroup();

  assert.equal(store.getState().past.length, 1);
  store.getState().undo();
  assert.equal(store.getState().document.name, "Initial");
});

test("history keeps only the latest one hundred entries", () => {
  const store = createAgentflowEditorStore(createDocument());
  for (let index = 0; index < 105; index += 1) {
    store.getState().updateDocument((document) => rename(document, `Name ${index}`));
  }

  assert.equal(store.getState().past.length, 100);
  for (let index = 0; index < 100; index += 1) {
    store.getState().undo();
  }
  assert.equal(store.getState().document.name, "Name 4");
});

test("composite graph edits are restored atomically", () => {
  const store = createAgentflowEditorStore(createDocument());
  store.getState().selectNode("agent");
  store.getState().updateDocument((document) => ({
    ...document,
    nodes: document.nodes.filter((node) => node.id !== "agent"),
    edges: document.edges.filter((edge) => edge.source !== "agent" && edge.target !== "agent"),
  }));

  assert.deepEqual(
    store.getState().document.nodes.map((node) => node.id),
    ["input"],
  );
  assert.equal(store.getState().document.edges.length, 0);
  assert.equal(store.getState().selectedNodeId, null);

  store.getState().undo();
  assert.deepEqual(
    store.getState().document.nodes.map((node) => node.id),
    ["input", "agent"],
  );
  assert.equal(store.getState().document.edges.length, 1);
});

test("invalid block scope and selection are reconciled after edits", () => {
  const document = createDocument();
  document.nodes.push(createNode("block", AgentflowNodeKind.ConcurrentBlock));
  const store = createAgentflowEditorStore(document);
  store.getState().setCanvasScope({ kind: "block", blockId: "block" });
  store.getState().selectNode("block");

  store.getState().updateDocument((current) => ({
    ...current,
    nodes: current.nodes.filter((node) => node.id !== "block"),
  }));

  assert.deepEqual(store.getState().canvasScope, { kind: "root" });
  assert.equal(store.getState().selectedNodeId, null);

  store.getState().undo();
  store.getState().setCanvasScope({ kind: "block", blockId: "block" });
  store.getState().selectNode("block");
  store.getState().redo();

  assert.deepEqual(store.getState().canvasScope, { kind: "root" });
  assert.equal(store.getState().selectedNodeId, null);
});

test("an automatic layout is one undoable document change", () => {
  const store = createAgentflowEditorStore(createDocument());
  store.getState().updateDocument((document) => ({
    ...document,
    nodes: document.nodes.map((node, index) => ({
      ...node,
      position: { x: index * 120, y: index * 80 },
    })),
  }));

  assert.equal(store.getState().past.length, 1);
  assert.deepEqual(store.getState().document.nodes[1]?.position, { x: 120, y: 80 });

  store.getState().undo();
  assert.deepEqual(store.getState().document.nodes[1]?.position, { x: 0, y: 0 });
});

test("ephemeral React Flow state does not mark the document dirty", () => {
  const store = createAgentflowEditorStore(createDocument());
  store.getState().updateDocument(
    (document) => ({
      ...document,
      nodes: document.nodes.map((node) =>
        node.id === "agent" ? { ...node, width: 220, selected: true } : node,
      ),
    }),
    "ephemeral",
  );

  assert.equal(store.getState().isDirty, false);
  assert.equal(selectCanUndo(store.getState()), false);
});

test("stores are isolated and markSaved resets only the current baseline", () => {
  const first = createAgentflowEditorStore(createDocument());
  const second = createAgentflowEditorStore(createDocument());
  first.getState().updateDocument((document) => rename(document, "Saved"));
  first.getState().markSaved();

  assert.equal(first.getState().isDirty, false);
  assert.equal(first.getState().document.name, "Saved");
  assert.equal(second.getState().document.name, "Initial");
});
