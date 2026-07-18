import type { components } from "@agw/api";
import type { ChatTargetOption } from "@agw/api";
import type { CommandSource } from "./search-command";

export type AgentSuggestionsResponse = components["schemas"]["AgentSuggestionsResponse"];

export function getAgentSuggestionQueryParams(
  projectId: string | null,
  target: (Pick<ChatTargetOption, "id" | "type"> & Partial<Pick<ChatTargetOption, "label">>) | null,
): { projectId?: string; agentId: string } | null {
  if (!target || target.type !== "agent") {
    return null;
  }

  return projectId ? { projectId, agentId: target.id } : { agentId: target.id };
}

export function toCommandSource(
  response: AgentSuggestionsResponse | undefined,
  claudeCommands: string[],
): CommandSource {
  if (!response || response.mode === "unsupported") {
    return { mode: "unsupported" };
  }

  if (response.mode === "claudeCode") {
    return { mode: "claudeCode", slashCommands: claudeCommands };
  }

  return { mode: "system", suggestions: response.suggestions };
}
