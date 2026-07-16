import type {
  AgwAgent,
  AgwAgentflow,
  AgwTarget,
} from "../../../api/agw-api-types";

const RESTRICTED_PROJECT_AGENT_MAP: Readonly<Record<string, string>> = {
  "11111111-1111-1111-1111-000000000002": "ClaudeCode",
  "11111111-1111-1111-1111-000000000004": "Codex",
};

type BuildAgwTargetOptionsInput = {
  projectId: string | null;
  agents: AgwAgent[];
  agentflows: AgwAgentflow[];
};

export function buildAgwTargetOptions({
  projectId,
  agents,
  agentflows,
}: BuildAgwTargetOptionsInput): AgwTarget[] {
  const restrictedAgentName =
    typeof projectId === "string"
      ? RESTRICTED_PROJECT_AGENT_MAP[projectId]
      : undefined;

  const filteredAgents = restrictedAgentName
    ? agents.filter((agent) => agent.name === restrictedAgentName)
    : agents;

  const agentOptions = filteredAgents.map((agent) => ({
    agentType: 0 as const,
    id: agent.id,
    label: agent.displayName?.trim() || agent.name,
    type: "agent" as const,
  }));

  const agentflowOptions = restrictedAgentName
    ? []
    : agentflows.map((agentflow) => ({
        agentType: 1 as const,
        id: agentflow.id,
        label: agentflow.name,
        type: "agentflow" as const,
      }));

  return [...agentOptions, ...agentflowOptions].sort((left, right) =>
    left.label.localeCompare(right.label)
  );
}

export function getTargetValue(target: Pick<AgwTarget, "id" | "type">): string {
  return `${target.type}:${target.id}`;
}
