import assert from "node:assert/strict";
import test from "node:test";
import { createEditorDocument } from "./agentflow-document";
import { AGENTFLOW_TEMPLATES } from "./agentflow-templates";
import { createAgentflowDefinition } from "./agentflow-transfer";

test("every template round-trips with one Input and no Agent selected", () => {
  assert.equal(AGENTFLOW_TEMPLATES.length, 5);
  for (const template of AGENTFLOW_TEMPLATES) {
    const document = { name: template.name, description: "", ...template.create() };
    assert.equal(document.nodes.filter((node) => node.kind === "input").length, 1);
    for (const node of document.nodes) {
      if (node.kind === "agent") assert.equal(node.relateId, null);
    }
    assert.deepEqual(createEditorDocument(document, createAgentflowDefinition(document)), document);
  }
});
