import type { ChatTargetOption } from "@agw/api";

import { getDefaultChatTargetValue, groupChatTargets } from "@/features/chat/chat-targets";

const targets: ChatTargetOption[] = [
  { id: "flow-1", label: "cc-codex", type: "agentflow" },
  { id: "agent-1", label: "Claude Code", type: "agent" },
  { id: "flow-2", label: "coding", type: "agentflow" },
  { id: "agent-2", label: "General Agent", type: "agent" },
];

test("chat targets are displayed in Agent and Agentflow groups", () => {
  expect(groupChatTargets(targets)).toEqual([
    {
      label: "Agent",
      type: "agent",
      targets: [targets[1], targets[3]],
    },
    {
      label: "Agentflow",
      type: "agentflow",
      targets: [targets[0], targets[2]],
    },
  ]);
});

test("the default target prefers an Agent and falls back to Agentflow", () => {
  expect(getDefaultChatTargetValue(targets)).toBe("agent:agent-1");
  expect(getDefaultChatTargetValue([targets[0], targets[2]])).toBe("agentflow:flow-1");
  expect(getDefaultChatTargetValue([])).toBeNull();
});
