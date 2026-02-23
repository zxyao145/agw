import { ApiError } from "@/api/client";
import type { AiMessage } from "@/types";
import type { AgentflowDetailDto, AgentflowNodeDto, AgentflowEdgeDto } from "@/types/agentflow";
import { apiGet } from "@/api/client";

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
  return message.contents.find((c) => c.type === "TextContent")?.content || "";
}

export function mergeTextContent(existing: AiMessage, incoming: AiMessage): void {
  const existingText = existing.contents.find((c) => c.type === "text");
  const incomingText = incoming.contents.find((c) => c.type === "text");

  if (existingText && incomingText) {
    existingText.content = (existingText.content || "") + (incomingText.content || "");
  }
}

export function mergeMessages(messages: AiMessage[]): AiMessage[] {
  const messageMap = new Map<string, AiMessage>();

  messages.forEach((msg) => {
    const existing = messageMap.get(msg.messageId);
    if (existing) {
      mergeTextContent(existing, msg);
    } else {
      messageMap.set(msg.messageId, { ...msg });
    }
  });

  return Array.from(messageMap.values());
}

export async function fetchAgentflowDetails(id: string): Promise<AgentflowDetailDto> {
  const [nodes, edges] = await Promise.all([
    // @ts-expect-error - OpenAPI schema has incorrect top-level path parameters definition
    apiGet("/api/agentflows/{id}/nodes", {
      params: { path: { id } },
    }),
    // @ts-expect-error - OpenAPI schema has incorrect top-level path parameters definition
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
