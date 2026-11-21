import type { ExecutionMessage } from "./message";

export type ProcessedMessageItem<T extends ExecutionMessage = ExecutionMessage> =
  | { type: "accordion"; messages: T[]; toolName: string }
  | { type: "normal"; message: T }
  | { type: "result"; message: T };

export type MessageFragmentType = "normal" | "result" | "function-call" | "function-result";

export type MessageFragment<T extends ExecutionMessage = ExecutionMessage> = {
  type: MessageFragmentType;
  message: T;
  callId: string | null;
  groupKey: string | null;
  toolName: string;
};
