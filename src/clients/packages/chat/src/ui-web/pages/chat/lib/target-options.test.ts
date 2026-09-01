import assert from "node:assert/strict";
import test from "node:test";

import { buildChatTargetOptions } from "./target-options";

test("restricted project only exposes the ClaudeCode agent", () => {
  const result = buildChatTargetOptions({
    projectId: "11111111-1111-1111-1111-000000000002",
    agents: [
      { id: "agent-1", name: "GeneralAgent", displayName: "General Agent" },
      { id: "agent-2", name: "ClaudeCode", displayName: "Claude Code" },
    ],
    agentflows: [{ id: "flow-1", name: "Team Flow" }],
  });

  assert.deepEqual(result, [{ id: "agent-2", label: "Claude Code", type: "agent" }]);
});

test("normal project exposes agents first and sorts each target group by label", () => {
  const result = buildChatTargetOptions({
    projectId: "11111111-1111-1111-1111-000000000099",
    agents: [
      { id: "agent-2", name: "ClaudeCode", displayName: "Claude Code" },
      { id: "agent-1", name: "GeneralAgent", displayName: "General Agent" },
    ],
    agentflows: [
      { id: "flow-2", name: "Zeta Flow" },
      { id: "flow-1", name: "Alpha Flow" },
      { id: "flow-3", name: "Beta Flow" },
    ],
  });

  assert.deepEqual(result, [
    { id: "agent-2", label: "Claude Code", type: "agent" },
    { id: "agent-1", label: "General Agent", type: "agent" },
    { id: "flow-1", label: "Alpha Flow", type: "agentflow" },
    { id: "flow-3", label: "Beta Flow", type: "agentflow" },
    { id: "flow-2", label: "Zeta Flow", type: "agentflow" },
  ]);
});
