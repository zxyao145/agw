import assert from "node:assert/strict";
import test from "node:test";

import {
  buildChatTargetOptions,
  getTargetValue,
  getTargetValueFromMetadata,
  parseTargetValue,
} from "./chat-target-options";

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
      projectId: "11111111-1111-1111-1111-000000000002",
      agents: [
        { id: "agent-1", name: "GeneralAgent", displayName: "General Agent", enable: true },
        { id: "agent-2", name: "ClaudeCode", displayName: "Claude Code", enable: true },
      ],
      agentflows: [{ id: "flow-1", name: "Team Flow", enable: true }],
    }),
    [{ id: "agent-2", label: "Claude Code", type: "agent" }],
  );
});

test("buildChatTargetOptions puts agents first and sorts each target group by label", () => {
  assert.deepEqual(
    buildChatTargetOptions({
      projectId: "11111111-1111-1111-1111-000000000099",
      agents: [
        { id: "agent-2", name: "ClaudeCode", displayName: "Claude Code", enable: true },
        { id: "agent-1", name: "GeneralAgent", displayName: "General Agent", enable: true },
      ],
      agentflows: [
        { id: "flow-2", name: "Zeta Flow", enable: true },
        { id: "flow-1", name: "Alpha Flow", enable: true },
        { id: "flow-3", name: "Beta Flow", enable: true },
      ],
    }),
    [
      { id: "agent-2", label: "Claude Code", type: "agent" },
      { id: "agent-1", label: "General Agent", type: "agent" },
      { id: "flow-1", label: "Alpha Flow", type: "agentflow" },
      { id: "flow-3", label: "Beta Flow", type: "agentflow" },
      { id: "flow-2", label: "Zeta Flow", type: "agentflow" },
    ],
  );
});

test("buildChatTargetOptions restricts codex project to codex agent only", () => {
  assert.deepEqual(
    buildChatTargetOptions({
      projectId: "11111111-1111-1111-1111-000000000004",
      agents: [
        { id: "agent-1", name: "GeneralAgent", displayName: "General Agent", enable: true },
        { id: "agent-2", name: "Codex", displayName: "OpenAI Codex", enable: true },
      ],
      agentflows: [{ id: "flow-1", name: "Team Flow", enable: true }],
    }),
    [{ id: "agent-2", label: "OpenAI Codex", type: "agent" }],
  );
});

test("buildChatTargetOptions excludes disabled agents and agentflows", () => {
  assert.deepEqual(
    buildChatTargetOptions({
      projectId: null,
      agents: [
        { id: "agent-1", name: "EnabledAgent", enable: true },
        { id: "agent-2", name: "DisabledAgent", enable: false },
      ],
      agentflows: [
        { id: "flow-1", name: "Enabled Flow", enable: true },
        { id: "flow-2", name: "Disabled Flow", enable: false },
      ],
    }),
    [
      { id: "agent-1", label: "EnabledAgent", type: "agent" },
      { id: "flow-1", label: "Enabled Flow", type: "agentflow" },
    ],
  );
});
