import assert from "node:assert/strict";
import test from "node:test";

import { createEdge, createEmptyDocument, createNode } from "./agentflow-document";
import { createGraphLayout } from "./auto-layout";

test("layout uses the 20px grid and reserves space for edge labels", async () => {
  const document = createEmptyDocument();
  const output = createNode("output", { x: 0, y: 0 });
  const labelText = "an expected result long enough to require more space around the label";
  document.nodes.push(output);
  document.edges.push({
    ...createEdge(document, "input", output.nodeId),
    conditions: [{ field: "contains", value: labelText, path: "" }],
  });
  const positions = await createGraphLayout(
    document,
    { input: { width: 220, height: 80 }, [output.nodeId]: { width: 220, height: 80 } },
    { width: 1800, height: 120 },
  );
  for (const { x, y } of Object.values(positions)) {
    assert.equal(x % 20, 0);
    assert.equal(y % 20, 0);
  }
  const betweenLayers = positions[output.nodeId].x - positions.input.x - 220;
  assert.ok(betweenLayers >= labelText.length * 6.2 + 18, JSON.stringify(positions));
});
