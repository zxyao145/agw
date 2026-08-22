import type { AiMessage } from "@agw/api";
import {
  collapseConsecutiveSystemMessages,
  getClaudeInitCommands,
  isResultMessage,
  prepareClaudeHistory,
} from "@agw/chat-core";

export {
  collapseConsecutiveSystemMessages,
  getClaudeInitCommands,
  isResultMessage,
  prepareClaudeHistory,
};

const ErrorContent = "ErrorContent";
const TextContent = "TextContent";
export type AiMessageAction =
  | { type: "append"; message: AiMessage }
  | { type: "setClaudeCommands"; commands: string[] }
  | { type: "setIsExecuting"; value: boolean };

export function handleSystemMessage(message: AiMessage): AiMessageAction[] {
  const initCommands = getClaudeInitCommands(message);
  if (initCommands !== null) {
    return [{ type: "setClaudeCommands", commands: initCommands }];
  }

  const firstContent = message.contents[0];
  if (!firstContent) {
    return [];
  }

  if (message.author === "Agw" && firstContent.type === ErrorContent) {
    return [
      { type: "setIsExecuting", value: false },
      { type: "append", message },
    ];
  }

  if (
    message.additionalProperties?.subtype === "hint" &&
    firstContent.type === TextContent &&
    typeof firstContent.content === "string" &&
    firstContent.content.toLowerCase().includes("interrupted")
  ) {
    return [{ type: "setIsExecuting", value: false }];
  }

  if (isResultMessage(message)) {
    return [
      { type: "setIsExecuting", value: false },
      { type: "append", message },
    ];
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
