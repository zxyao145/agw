import type { AiMessage } from "@agw/api";
import { parseClaudeInitCommands } from "./claude-commands";
import { isSystemInjectedMessage } from "./message-source";

export type MessageMeta = {
  name: string | null;
  author: string | null;
};

export const MESSAGE_PREVIEW_MAX_LENGTH = 72;
const AGENT_NAME_KEYS = ["nodeName", "name", "agentName", "displayName", "agentDisplayName"];
const HISTORY_CONTROL_MESSAGE_TYPES = new Set([
  "turn-start",
  "turn-finished",
  "human-gate-request",
  "tool-approval-request",
  "human-interaction-request",
]);
const STANDALONE_SYSTEM_MESSAGE_TYPES = new Set([
  "tool-todo-snapshot",
  "tool-mode-status",
  "tool-background-task-status",
  "tool-warning",
  "turn.started",
  "agentflow-checkpoint",
]);

function readString(value: unknown): string | null {
  return typeof value === "string" && value.trim() ? value.trim() : null;
}

function readStringProperty(message: AiMessage, keys: string[]): string | null {
  const messageRecord = message as unknown as Record<string, unknown>;

  for (const key of keys) {
    const value = message.additionalProperties?.[key] ?? messageRecord[key];
    const text = readString(value);
    if (text) return text;
  }

  return null;
}

export function isResultMessage(message: AiMessage): boolean {
  return (
    message.additionalProperties?.type === "result" ||
    message.contents.some((content) => content.additionalProperties?.type === "result")
  );
}

export function getMessageMeta(message: AiMessage): MessageMeta | null {
  if (isResultMessage(message)) return null;

  const agentAuthor = readString(message.author);
  if (message.role === "user") {
    return agentAuthor ? { name: null, author: agentAuthor } : null;
  }

  const agentName = readStringProperty(message, AGENT_NAME_KEYS);
  if (!agentName && !agentAuthor) return null;

  return {
    name: agentName,
    author: agentAuthor,
  };
}

function isReadOnlyModeSnapshot(message: AiMessage): boolean {
  return (
    message.additionalProperties?.type === "tool-mode-status" &&
    message.additionalProperties?.toolName === "mode_get"
  );
}

export function collapseConsecutiveSystemMessages(messages: readonly AiMessage[]): AiMessage[] {
  const visibleMessages = messages.filter(
    (message) => !isReadOnlyModeSnapshot(message) && !parseClaudeInitCommands(message).isInit,
  );
  return visibleMessages.filter(
    (message, index) =>
      message.role !== "system" ||
      isResultMessage(message) ||
      STANDALONE_SYSTEM_MESSAGE_TYPES.has(String(message.additionalProperties?.type)) ||
      visibleMessages[index + 1]?.role !== "system" ||
      (visibleMessages[index + 1] ? isResultMessage(visibleMessages[index + 1]) : false) ||
      STANDALONE_SYSTEM_MESSAGE_TYPES.has(
        String(visibleMessages[index + 1]?.additionalProperties?.type),
      ),
  );
}

export function prepareClaudeHistory(messages: readonly AiMessage[]): {
  messages: AiMessage[];
  commands: string[];
} {
  const visibleMessages: AiMessage[] = [];
  let commands: string[] = [];
  let foundValidInit = false;

  for (const message of messages) {
    const init = parseClaudeInitCommands(message);
    if (!init.isInit) {
      if (
        !isReadOnlyModeSnapshot(message) &&
        !isSystemInjectedMessage(message) &&
        !HISTORY_CONTROL_MESSAGE_TYPES.has(String(message.additionalProperties?.type)) &&
        message.additionalProperties?.presentation !== "control"
      ) {
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

type JsonParseResult = { parsed: true; value: unknown } | { parsed: false };

function tryParseJson(value: string): JsonParseResult {
  try {
    return { parsed: true, value: JSON.parse(value) };
  } catch {
    return { parsed: false };
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function stringifyJsonValue(value: unknown): string {
  if (typeof value === "string") return value;
  return JSON.stringify(value, null, 4) ?? "";
}

function findClaudeHookEventName(value: unknown, depth: number): string | null {
  if (depth > 4) return null;

  if (Array.isArray(value)) {
    for (const item of value) {
      const eventName = findClaudeHookEventName(item, depth + 1);
      if (eventName) return eventName;
    }
    return null;
  }

  if (isRecord(value)) {
    if (value.type === "system") {
      const eventName = readString(value.hook_event);
      if (eventName) return eventName;
    }

    return "content" in value ? findClaudeHookEventName(value.content, depth + 1) : null;
  }

  if (typeof value !== "string") return null;

  const parsed = tryParseJson(value);
  if (parsed.parsed) {
    return findClaudeHookEventName(parsed.value, depth + 1);
  }

  if (!/"type"\s*:\s*"system"/.test(value)) return null;

  const eventMatch = /"hook_event"\s*:\s*("(?:\\.|[^"\\])*")/.exec(value);
  if (!eventMatch) return null;

  const eventName = tryParseJson(eventMatch[1]);
  return eventName.parsed ? readString(eventName.value) : null;
}

export function getClaudeHookEventName(content: string): string | null {
  return findClaudeHookEventName(content, 0);
}

function formatNestedJson(input: unknown, keys: readonly string[]): string {
  let jsonObject: Record<string, unknown>;
  if (typeof input === "string") {
    const parsed = tryParseJson(input);
    if (!parsed.parsed || !isRecord(parsed.value)) return input;
    jsonObject = parsed.value;
  } else if (isRecord(input)) {
    jsonObject = input;
  } else {
    return stringifyJsonValue(input);
  }

  if (keys.length === 0) {
    return JSON.stringify(input, null, 4) ?? "";
  }

  const [key, ...remainingKeys] = keys;
  if (!jsonObject[key]) {
    return JSON.stringify(input, null, 4) ?? "";
  }

  return formatNestedJson(jsonObject[key], remainingKeys);
}

export function formatSystemMessageContent(content: string): string {
  const directHookEvent = getClaudeHookEventName(content);
  if (directHookEvent) return directHookEvent;

  const parsed = tryParseJson(content);
  if (!parsed.parsed) return content;
  if (!isRecord(parsed.value)) return stringifyJsonValue(parsed.value);

  if (!parsed.value.output) {
    return stringifyJsonValue(parsed.value);
  }

  return formatNestedJson(parsed.value.output, ["hookSpecificOutput", "hookEventName"]);
}

export function getMessagePreview(content: string, maxLength = MESSAGE_PREVIEW_MAX_LENGTH): string {
  const firstLine = content.split(/\r?\n/, 1)[0] || "thinking";

  if (firstLine.length <= maxLength) return firstLine;

  return `${firstLine.slice(0, maxLength - 1).trimEnd()}…`;
}
