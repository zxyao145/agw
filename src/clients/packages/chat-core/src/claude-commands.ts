import type { AiMessage } from "@agw/api";

export type ClaudeInitParseResult =
  | { isInit: false }
  | { isInit: true; isValid: boolean; commands: string[] };

export function parseClaudeInitCommands(message: AiMessage): ClaudeInitParseResult {
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

export function getClaudeInitCommands(message: AiMessage): string[] | null {
  const result = parseClaudeInitCommands(message);
  return result.isInit ? result.commands : null;
}

export function getClaudeHistoryCommands(messages: readonly AiMessage[]): string[] {
  let commands: string[] = [];

  for (const message of messages) {
    const result = parseClaudeInitCommands(message);
    if (result.isInit && result.isValid) {
      commands = result.commands;
    }
  }

  return commands;
}
