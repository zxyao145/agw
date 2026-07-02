import type { AiMessage } from "@/types";
import type { AgentflowDetailDto, AgentflowNodeDto, AgentflowEdgeDto } from "@/types/agentflow";
import { apiGet } from "@/api/client";
import {
  getMessageTextContent,
  mergeStreamingMessage,
  mergeStreamingMessagesById,
} from "@/lib/execution-stream";

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
    nodes: (nodes as unknown as AgentflowNodeDto[]) || [],
    edges: (edges as unknown as AgentflowEdgeDto[]) || [],
  } as AgentflowDetailDto;
}
