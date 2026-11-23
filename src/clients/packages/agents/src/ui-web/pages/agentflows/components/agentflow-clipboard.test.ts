import assert from "node:assert/strict";
import test from "node:test";
import { copyNodes, deleteEdge, deleteNodes, pasteNodes } from "./agentflow-clipboard";
import { createEdge, createEmptyDocument, createNode } from "./agentflow-document";

test("pasted nodes, edges, members and choices get new ids and consistent references", () => {
  const document = createEmptyDocument();
  const gate = createNode("humanGate", { x: 40, y: 60 });
  const block = createNode("magenticBlock", { x: 280, y: 80 });
  assert.equal(gate.kind, "humanGate");
  assert.equal(block.kind, "magenticBlock");
  block.members = [
    {
      memberId: "manager",
      name: "Manager",
      kind: "agent",
      relateId: "agent",
      instructions: null,
      isManager: true,
    },
  ];
  document.nodes.push(gate, block);
  document.edges.push(createEdge(document, gate.nodeId, block.nodeId));
  const clipboard = copyNodes(document, ["input", gate.nodeId, block.nodeId]);
  assert.ok(clipboard);

  const { document: updated, nodeIds } = pasteNodes(document, clipboard, { offset: 40 });
  const [newGate, newBlock] = updated.nodes.slice(-2);
  assert.equal(newGate.kind, "humanGate");
  assert.equal(newBlock.kind, "magenticBlock");
  assert.deepEqual(nodeIds, [newGate.nodeId, newBlock.nodeId]);
  assert.notEqual(newGate.nodeId, gate.nodeId);
  assert.notEqual(newBlock.nodeId, block.nodeId);
  assert.notEqual(newGate.choices[0].choiceId, gate.choices[0].choiceId);
  assert.notEqual(newBlock.members[0].memberId, block.members[0].memberId);
  assert.equal(newBlock.members[0].relateId, block.members[0].relateId);
  assert.equal(newBlock.members[0].isManager, true);
  assert.notEqual(updated.edges[1].edgeId, document.edges[0].edgeId);
  assert.equal(updated.edges[1].sourceNodeId, newGate.nodeId);
  assert.equal(updated.edges[1].targetNodeId, newBlock.nodeId);
  assert.equal(updated.edges[1].choiceId, newGate.choices[0].choiceId);
  assert.deepEqual(newGate.position, { x: 80, y: 100 });
  const atPointer = pasteNodes(document, clipboard, { position: { x: 500, y: 100 } });
  assert.deepEqual(atPointer.document.nodes.at(-2)?.position, { x: 500, y: 100 });
  assert.deepEqual(atPointer.document.nodes.at(-1)?.position, { x: 740, y: 120 });
});

test("Input is neither copied nor deleted", () => {
  const document = createEmptyDocument();
  assert.equal(copyNodes(document, ["input"]), null);
  assert.equal(deleteNodes(document, ["input"]), document);
});

test("deleting a branch renumbers the remaining branches and deleting a node removes its edges", () => {
  const document = createEmptyDocument();
  const targets = Array.from({ length: 3 }, (_, index) =>
    createNode("output", { x: 260, y: 80 + index * 120 }),
  );
  document.nodes[0].routingStrategy = "switch";
  document.nodes.push(...targets);
  for (const target of targets) document.edges.push(createEdge(document, "input", target.nodeId));

  const withoutEdge = deleteEdge(document, document.edges[1].edgeId);
  assert.deepEqual(
    withoutEdge.edges.map((edge) => edge.switchCaseOrder),
    [0, 1],
  );
  const withoutNode = deleteNodes(document, [targets[0].nodeId]);
  assert.equal(withoutNode.nodes.length, 3);
  assert.deepEqual(
    withoutNode.edges.map((edge) => edge.targetNodeId),
    targets.slice(1).map((node) => node.nodeId),
  );
  assert.deepEqual(
    withoutNode.edges.map((edge) => edge.switchCaseOrder),
    [0, 1],
  );
});
