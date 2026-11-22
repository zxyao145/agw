import assert from "node:assert/strict";
import test from "node:test";
import {
  createEdge,
  createEditorDocument,
  createEmptyDocument,
  createNode,
} from "./agentflow-document";
import {
  createAgentflowDefinition,
  createAgentflowTransfer,
  parseAgentflowTransfer,
} from "./agentflow-transfer";

test("an exported document can be imported without changes", () => {
  const document = { ...createEmptyDocument(), name: "Example", description: "Send a result" };
  const output = createNode("output", { x: 400, y: 80 });
  document.nodes.push(output);
  document.edges.push(createEdge(document, "input", output.nodeId));
  const transfer = createAgentflowTransfer(document);
  assert.deepEqual(createEditorDocument(transfer, createAgentflowDefinition(document)), document);
  assert.deepEqual(parseAgentflowTransfer(JSON.stringify(transfer)), { ok: true, transfer });
});

test("imports report missing arrays and invalid JSON", () => {
  assert.deepEqual(parseAgentflowTransfer("{}"), {
    ok: false,
    errors: [
      { field: "definition.nodes", value: "" },
      { field: "definition.edges", value: "" },
    ],
  });
  assert.deepEqual(parseAgentflowTransfer("invalid json"), {
    ok: false,
    errors: [{ field: "json", value: "" }],
  });
});

test("duplicates are reported per ID group, with case-sensitive matching", () => {
  const document = createEmptyDocument();
  const output = { ...createNode("output", { x: 400, y: 80 }), nodeId: "result" };
  document.nodes.push(output);
  const edge = { ...createEdge(document, "input", output.nodeId), edgeId: "result" };
  document.edges.push(edge);
  assert.equal(parseAgentflowTransfer(JSON.stringify(createAgentflowTransfer(document))).ok, true);
  document.nodes.push({ ...output, nodeId: "Result" }, output);
  document.edges.push({ ...edge, edgeId: "Result" }, edge);
  assert.deepEqual(parseAgentflowTransfer(JSON.stringify(createAgentflowTransfer(document))), {
    ok: false,
    errors: [
      { field: "nodeId", value: "result" },
      { field: "edgeId", value: "result" },
    ],
  });
});

test("an unknown kind is reported with its nodeId and kind", () => {
  assert.deepEqual(
    parseAgentflowTransfer(
      JSON.stringify({ definition: { nodes: [{ nodeId: "review", kind: "legacy" }], edges: [] } }),
    ),
    { ok: false, errors: [{ field: "kind", value: "review: legacy" }] },
  );
});
