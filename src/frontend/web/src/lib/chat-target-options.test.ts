import assert from "node:assert/strict";
import test from "node:test";

import {
  SPECIAL_PROJECT_ID,
  buildChatTargetOptions,
  getTargetValue,
  getTargetValueFromMetadata,
  parseTargetValue,
} from "./chat-target-options.ts";

test("getTargetValue encodes agent and agentflow targets", () => {
  assert.equal(getTargetValue({ id: "agent-1", type: "agent" }), "agent:agent-1");
  assert.equal(getTargetValue({ id: "flow-9", type: "agentflow" }), "agentflow:flow-9");
});

test("parseTargetValue decodes valid target values and rejects invalid input", () => {
  assert.deepEqual(parseTargetValue("agent:agent-1"), { id: "agent-1", type: "agent" });
  assert.deepEqual(parseTargetValue("agentflow:flow-9"), { id: "flow-9", type: "agentflow" });
  assert.equal(parseTargetValue("agent:"), null);
  assert.equal(parseTargetValue("unknown:123"), null);
});

test("getTargetValueFromMetadata rebuilds target values from persisted metadata", () => {
  assert.equal(getTargetValueFromMetadata("agent", "agent-1"), "agent:agent-1");
  assert.equal(getTargetValueFromMetadata("agentflow", "flow-9"), "agentflow:flow-9");
  assert.equal(getTargetValueFromMetadata("agent", ""), null);
});

test("buildChatTargetOptions preserves restricted-project filtering and sorting", () => {
  assert.deepEqual(
    buildChatTargetOptions({
      projectId: SPECIAL_PROJECT_ID,
      agents: [
        { id: "agent-1", name: "GeneralAgent", displayName: "General Agent" },
        { id: "agent-2", name: "ClaudeCode", displayName: "Claude Code" },
      ],
      agentflows: [{ id: "flow-1", name: "Team Flow", enable: true }],
    }),
    [{ id: "agent-2", label: "Claude Code", type: "agent" }],
  );
});
