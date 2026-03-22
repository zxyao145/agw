import { ApiError } from "@/api/client";
import type { AiMessage } from "@/types";
import type { AgentflowDetailDto, AgentflowNodeDto, AgentflowEdgeDto } from "@/types/agentflow";
import { apiGet } from "@/api/client";
import {
  getMessageTextContent,
  mergeStreamingMessage,
  mergeStreamingMessagesById,
} from "@/lib/execution-stream";

export function getApiErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (typeof error.body === "string" && error.body.trim().length) {
      return error.body;
    }
    return `${error.status} ${error.statusText}`;
  }
  if (error instanceof Error) return error.message;
  return "Unknown error";
}

export function getPatternName(pattern: number): string {
  const PATTERN_NAMES: Record<number, string> = {
    0: "Concurrent",
    1: "Sequential",
    2: "GroupChat",
    3: "Handoff",
    4: "Magentic",
  };
  return PATTERN_NAMES[pattern] ?? `Unknown (${pattern})`;
}

export function getTextContent(message: AiMessage): string {
  return getMessageTextContent(message);
}

export function mergeTextContent(existing: AiMessage, incoming: AiMessage): void {
  const merged = mergeStreamingMessage([existing], incoming);
  if (merged.length > 0) {
    existing.contents = merged[0].contents;
  }
}

export function mergeMessages(messages: AiMessage[]): AiMessage[] {
  return mergeStreamingMessagesById(messages);
}

export async function fetchAgentflowDetails(id: string): Promise<AgentflowDetailDto> {
  const [nodes, edges] = await Promise.all([
    apiGet("/api/agentflows/{id}/nodes", {
      params: { path: { id } },
    }),
    apiGet("/api/agentflows/{id}/edges", {
      params: { path: { id } },
    }),
  ]);

  return {
    id,
    nodes: (nodes as AgentflowNodeDto[]) || [],
    edges: (edges as AgentflowEdgeDto[]) || [],
  } as AgentflowDetailDto;
}
