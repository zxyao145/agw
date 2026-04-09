import type { ChatTargetOption, ChatTargetType } from "../types/chat-target";

export const SPECIAL_PROJECT_ID = "11111111-1111-1111-1111-000000000002";
const RESTRICTED_AGENT_NAME = "ClaudeCode";

type ChatAgentTargetSource = {
  id: string;
  name: string;
  displayName?: string | null;
};

type ChatAgentflowTargetSource = {
  id: string;
  name: string;
  enable?: boolean;
};

type BuildChatTargetOptionsInput = {
  projectId: string | null;
  agents: ChatAgentTargetSource[];
  agentflows: ChatAgentflowTargetSource[];
};

export function getTargetValue(target: Pick<ChatTargetOption, "id" | "type">): string {
  return `${target.type}:${target.id}`;
}

export function getTargetValueFromMetadata(targetType: unknown, targetId: unknown): string | null {
  if ((targetType !== "agent" && targetType !== "agentflow") || typeof targetId !== "string") {
    return null;
  }

  const trimmedTargetId = targetId.trim();
  if (!trimmedTargetId) {
    return null;
  }

  return `${targetType}:${trimmedTargetId}`;
}

export function parseTargetValue(
  value?: string | null,
): { id: string; type: ChatTargetType } | null {
  if (!value) {
    return null;
  }

  const [type, ...idSegments] = value.split(":");
  const id = idSegments.join(":").trim();

  if ((type !== "agent" && type !== "agentflow") || !id) {
    return null;
  }

  return { id, type };
}

export function buildChatTargetOptions({
  projectId,
  agents,
  agentflows,
}: BuildChatTargetOptionsInput): ChatTargetOption[] {
  const filteredAgents =
    projectId === SPECIAL_PROJECT_ID
      ? agents.filter((agent) => agent.name === RESTRICTED_AGENT_NAME)
      : agents;

  const agentOptions = filteredAgents.map((agent) => ({
    id: agent.id,
    label: agent.displayName?.trim() || agent.name,
    type: "agent" as const,
  }));

  const agentflowOptions =
    projectId === SPECIAL_PROJECT_ID
      ? []
      : agentflows
          .filter((agentflow) => agentflow.enable ?? true)
          .map((agentflow) => ({
            id: agentflow.id,
            label: agentflow.name,
            type: "agentflow" as const,
          }));

  return [...agentOptions, ...agentflowOptions].sort((left, right) =>
    left.label.localeCompare(right.label),
  );
}
