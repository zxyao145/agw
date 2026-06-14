import type { AiMessage } from "@/types";

const ErrorContent = "ErrorContent";
const TextContent = "TextContent";
const ResultType = "result";

export function isResultMessage(message: AiMessage): boolean {
  return (
    message.additionalProperties?.type === ResultType ||
    message.contents.some((content) => content.additionalProperties?.type === ResultType)
  );
}

export type AiMessageAction =
  | { type: "append"; message: AiMessage }
  | { type: "setIsExecuting"; value: boolean };

export function handleSystemMessage(message: AiMessage): AiMessageAction[] {
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
