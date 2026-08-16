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
  assert.match(source, /className="gap-0 p-0"/);
  assert.match(source, /<DialogHeader className="shrink-0 border-b px-6 py-2">/);
  assert.doesNotMatch(source, /fixed inset-0 w-screen h-screen/);
});

test("Agentflow edge inspector exposes deletion and ordered branch controls", async () => {
  const source = await readFile(BUILDER_URL, "utf8");

  assert.match(source, /aria-label="Delete edge"/);
  assert.match(source, /onDelete\(edge\.id\)/);
  assert.match(source, /removeAgentflowEdge\(document\.edges, edgeId\)/);
  assert.match(source, /onMoveSwitchCase\(edge\.id, -1\)/);
  assert.match(source, /onMoveSwitchCase\(edge\.id, 1\)/);
  assert.match(source, /If \/ Else If/);
  assert.match(source, /Fan-in Barrier/);
  assert.match(source, /Number\(value\) === AgentflowEdgeKind\.SwitchDefault && hasOtherDefault/);
});

test("Agentflow agent nodes default to the agent name and keep it editable", async () => {
  const source = await readFile(BUILDER_URL, "utf8");

  assert.match(
    source,
    /if \(agent\) onAddNode\(AgentflowNodeKind\.Agent, agent\.name, agent\.id\)/,
  );
  assert.match(
    source,
    /<Label>Name<\/Label>[\s\S]*?value=\{node\.data\.title\}[\s\S]*?\{ title: event\.target\.value \},[\s\S]*?\{ group: `node:\$\{node\.id\}:title` \}/,
  );
  assert.match(source, /name: node\.data\.title \|\| null/);
});

test("Agentflow Clear Messages and Checkpoint hide advanced JSON", async () => {
  const source = await readFile(BUILDER_URL, "utf8");

  assert.match(
    source,
    /\[AgentflowNodeKind\.ClearMessages\]: \{[\s\S]*?label: "Clear Messages",[\s\S]*?symbol: "Ø",[\s\S]*?body: "Discard upstream messages and continue with empty input",[\s\S]*?\}/,
  );
  assert.match(
    source,
    /label="Clear Messages"[\s\S]*?onClick=\{\(\) => onAddNode\(AgentflowNodeKind\.ClearMessages, "Clear Messages"\)\}/,
  );
  assert.match(
    source,
    /const usesInstructions =[\s\S]*?node\.data\.kind !== AgentflowNodeKind\.ClearMessages/,
  );
  assert.match(
    source,
    /const usesAdvancedConfig =\s*node\.data\.kind !== AgentflowNodeKind\.ClearMessages &&\s*node\.data\.kind !== AgentflowNodeKind\.CheckpointMarker/,
  );
  assert.match(source, /\{usesAdvancedConfig \? \([\s\S]*?<Label>Advanced Config JSON<\/Label>/);
});

test("Agentflow editor creates one Zustand store per dialog session", async () => {
  const source = await readFile(DIALOG_URL, "utf8");

  assert.match(source, /if \(!props\.open\) return null/);
  assert.match(
    source,
    /<AgentflowEditorProvider[\s\S]*?initialDocument=\{createAgentflowEditorDocument/,
  );
  assert.match(source, /<VisualAgentflowDialogSession \{\.\.\.props\} \/>/);
});

test("Agentflow editor exposes undo, redo, dirty status, and guarded close actions", async () => {
  const source = await readFile(DIALOG_URL, "utf8");

  assert.match(source, /aria-label="Undo"/);
  assert.match(source, /aria-label="Redo"/);
  assert.match(source, /Unsaved changes/);
  assert.match(source, /Discard unsaved changes\?/);
  assert.match(source, /Keep editing/);
  assert.match(source, /if \(isDirty\) \{[\s\S]*?setDiscardConfirmationOpen\(true\)/);
});

test("Agentflow editor keyboard history preserves native input undo", async () => {
  const source = await readFile(DIALOG_URL, "utf8");

  assert.match(source, /event\.metaKey \|\| event\.ctrlKey/);
  assert.match(source, /event\.ctrlKey && !event\.metaKey && key === "y"/);
  assert.match(source, /isEditableKeyboardTarget\(event\.target\)/);
  assert.match(source, /input, textarea, select, \[contenteditable\]/);
});

test("Agentflow editor groups text and drag history while saving only marks successful writes clean", async () => {
  const source = await readFile(BUILDER_URL, "utf8");

  assert.match(source, /onBlurCapture=\{commitHistoryGroup\}/);
  assert.match(source, /onNodeDragStart=\{commitHistoryGroup\}/);
  assert.match(source, /onNodeDragStop=\{commitHistoryGroup\}/);
  assert.match(source, /\{ group: "node-position" \}/);
  assert.match(
    source,
    /await api(?:Put|Post)[\s\S]*?markSaved\(\);[\s\S]*?onAgentflowCreated\?\.\(\)/,
  );
  assert.match(source, /catch \(error\) \{[\s\S]*?Failed to save agentflow/);
});
