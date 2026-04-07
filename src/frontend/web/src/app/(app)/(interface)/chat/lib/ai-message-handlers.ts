import type { AiMessage } from "@/types";
import { MessageContentType } from "@/types";

export type AiMessageAction =
  | { type: "append"; message: AiMessage }
  | { type: "setIsExecuting"; value: boolean };

export function handleSystemMessage(message: AiMessage): AiMessageAction[] {
  const firstContent = message.contents[0];
  if (!firstContent) {
    return [];
  }

  if (message.author === "Agw" && firstContent.type === MessageContentType.ErrorContent) {
    return [
      { type: "setIsExecuting", value: false },
      { type: "append", message },
    ];
  }

  if (
    message.additionalProperties?.subtype === "hint" &&
    firstContent.type === MessageContentType.TextContent &&
    firstContent.content.toLowerCase().includes("interrupted")
  ) {
    return [{ type: "setIsExecuting", value: false }];
  }

  if (message.additionalProperties?.type === "result") {
    return [{ type: "setIsExecuting", value: false }];
  }

  return [{ type: "append", message }];
}

export function handleAiMessage(message: AiMessage): AiMessageAction[] {
  if (message.role === "system") {
    return handleSystemMessage(message);
  }

  if (message.role === "assistant" || message.role === "user") {
    return [{ type: "append", message }];
  }

  return [];
}
