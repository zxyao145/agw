import type { components } from "@agw/api";

export type AgentflowNodeKind = components["schemas"]["AgentflowNodeDto"]["kind"];

export const AGENTFLOW_NODE_LABELS: Record<AgentflowNodeKind, string> = {
  input: "Input",
  agent: "Agent",
  workflowAsAgent: "Workflow as Agent",
  promptAdapter: "Prompt Adapter",
  clearMessages: "Clear Messages",
  humanGate: "Human Gate",
  checkpointMarker: "Checkpoint",
  output: "Output",
  concurrentBlock: "Concurrent Block",
  handoffBlock: "Handoff Group",
  groupChatBlock: "GroupChat Room",
  magenticBlock: "Magentic Team",
};
