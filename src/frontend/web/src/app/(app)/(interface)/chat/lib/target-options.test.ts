import assert from "node:assert/strict";
import test from "node:test";

import { SPECIAL_PROJECT_ID, buildChatTargetOptions } from "./target-options.ts";

test("restricted project only exposes the ClaudeCode agent", () => {
  const result = buildChatTargetOptions({
    projectId: SPECIAL_PROJECT_ID,
    agents: [
      { id: "agent-1", name: "GeneralAgent", displayName: "General Agent" },
      { id: "agent-2", name: "ClaudeCode", displayName: "Claude Code" },
    ],
    agentflows: [{ id: "flow-1", name: "Team Flow", enable: true }],
  });

  assert.deepEqual(result, [{ id: "agent-2", label: "Claude Code", type: "agent" }]);
});

test("normal project exposes all agents and enabled agentflows sorted by label", () => {
  const result = buildChatTargetOptions({
    projectId: "11111111-1111-1111-1111-000000000099",
    agents: [
      { id: "agent-2", name: "ClaudeCode", displayName: "Claude Code" },
      { id: "agent-1", name: "GeneralAgent", displayName: "General Agent" },
    ],
    agentflows: [
      { id: "flow-2", name: "Zeta Flow", enable: true },
      { id: "flow-1", name: "Alpha Flow", enable: false },
      { id: "flow-3", name: "Beta Flow", enable: true },
    ],
  });

  assert.deepEqual(result, [
    { id: "flow-3", label: "Beta Flow", type: "agentflow" },
    { id: "agent-2", label: "Claude Code", type: "agent" },
    { id: "agent-1", label: "General Agent", type: "agent" },
    { id: "flow-2", label: "Zeta Flow", type: "agentflow" },
  ]);
});
