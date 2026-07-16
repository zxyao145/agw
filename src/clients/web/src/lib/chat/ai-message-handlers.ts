import type { AiMessage } from "@/types";

const ErrorContent = "ErrorContent";
const TextContent = "TextContent";
const ResultType = "result";
const ControlMessageTypes = new Set(["turn-start", "turn-finished", "human-gate-request"]);

export function isResultMessage(message: AiMessage): boolean {
  return (
    message.additionalProperties?.type === ResultType ||
    message.contents.some((content) => content.additionalProperties?.type === ResultType)
  );
}

export function collapseConsecutiveSystemMessages(messages: AiMessage[]): AiMessage[] {
  return messages.filter(
    (message, index) => message.role !== "system" || messages[index + 1]?.role !== "system",
  );
}

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

export function getClaudeInitCommands(message: AiMessage): string[] | null {
  const result = parseClaudeInitMessage(message);
  return result.isInit ? result.commands : null;
}

export function prepareClaudeHistory(messages: AiMessage[]): {
  messages: AiMessage[];
  commands: string[];
} {
  const visibleMessages: AiMessage[] = [];
  let commands: string[] = [];
  let foundValidInit = false;

  for (const message of messages) {
    const init = parseClaudeInitMessage(message);
    if (!init.isInit) {
      if (!ControlMessageTypes.has(String(message.additionalProperties?.type))) {
        visibleMessages.push(message);
      }
      continue;
    }

    if (init.isValid) {
      commands = init.commands;
      foundValidInit = true;
    }
  }

  return {
    messages: visibleMessages,
    commands: foundValidInit ? commands : [],
  };
}

type ClaudeInitParseResult =
  | { isInit: false }
  | { isInit: true; isValid: boolean; commands: string[] };

function parseClaudeInitMessage(message: AiMessage): ClaudeInitParseResult {
  if (message.additionalProperties?.subtype !== "init") {
    return { isInit: false };
  }

  const rawContent = message.contents[0]?.content;
  let content: unknown = rawContent;
  if (typeof rawContent === "string") {
    try {
      content = JSON.parse(rawContent);
    } catch {
      return { isInit: true, isValid: false, commands: [] };
    }
  }

  if (typeof content !== "object" || content === null || Array.isArray(content)) {
    return { isInit: true, isValid: false, commands: [] };
  }

  const slashCommands = (content as Record<string, unknown>).slash_commands;
  if (!Array.isArray(slashCommands)) {
    return { isInit: true, isValid: false, commands: [] };
  }

  return {
    isInit: true,
    isValid: true,
    commands: slashCommands.filter((command): command is string => typeof command === "string"),
  };
}
