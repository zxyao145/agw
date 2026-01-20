import type { AiMessage } from "@/types";

export type AgentflowExecuteRequest = {
  threadId: string | null;
  input: string;
};

export type AgentflowExecuteResponse = {
  threadId: string;
  messages: AiMessage[];
};
