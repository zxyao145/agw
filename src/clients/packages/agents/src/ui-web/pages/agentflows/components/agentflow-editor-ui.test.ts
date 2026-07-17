import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const BUILDER_URL = new URL("./visual-agentflow-builder.tsx", import.meta.url);
const DIALOG_URL = new URL("./visual-agentflow-dialog.tsx", import.meta.url);

test("Agentflow node connection handles use the larger shared size", async () => {
  const source = await readFile(BUILDER_URL, "utf8");

  assert.match(
    source,
    /const HANDLE_STYLE: React\.CSSProperties = \{[\s\S]*?width: 18,[\s\S]*?height: 18,[\s\S]*?zIndex: 10,[\s\S]*?borderWidth: 3,[\s\S]*?borderColor: "var\(--background\)",[\s\S]*?\};/,
  );
  assert.match(
    source,
    /type="target"[\s\S]*?className="!bg-sky-600"[\s\S]*?style=\{HANDLE_IN_STYLE\}/,
  );
  assert.match(
    source,
    /type="source"[\s\S]*?className="!bg-emerald-600"[\s\S]*?style=\{HANDLE_OUT_STYLE\}/,
  );
});

test("Agentflow node handles render outside the clipped card surface", async () => {
  const source = await readFile(BUILDER_URL, "utf8");
  const dagNodeStart = source.indexOf("function DagNode");
  const dagNodeEnd = source.indexOf("function BlockParticipantSummary");

  assert.notEqual(dagNodeStart, -1);
  assert.notEqual(dagNodeEnd, -1);

  const dagNodeSource = source.slice(dagNodeStart, dagNodeEnd);
  const targetHandleIndex = dagNodeSource.indexOf('type="target"');
  const cardIndex = dagNodeSource.indexOf("<Card");
  const cardEndIndex = dagNodeSource.lastIndexOf("</Card>");
  const sourceHandleIndex = dagNodeSource.indexOf('type="source"');

  assert.notEqual(targetHandleIndex, -1);
  assert.notEqual(cardIndex, -1);
  assert.notEqual(cardEndIndex, -1);
  assert.notEqual(sourceHandleIndex, -1);
  assert.match(dagNodeSource, /<div className="relative w-\[220px\]">/);
  assert.ok(targetHandleIndex < cardIndex);
  assert.ok(sourceHandleIndex > cardEndIndex);
  assert.match(dagNodeSource, /<Card[\s\S]*?overflow-hidden/);
});

test("Agentflow uses the shared fullscreen Dialog contract", async () => {
  const source = await readFile(DIALOG_URL, "utf8");

  assert.match(source, /<DialogContent\s+size="fullscreen"/);
  assert.doesNotMatch(source, /fixed inset-0 w-screen h-screen/);
});
