import type { ChatTargetOption, components } from "@agw/api";
import Fuse from "fuse.js";

export interface SuggestionItem {
  text: string;
  kind?: string;
  description?: string;
}

export type AgentCommandSuggestion = SuggestionItem & {
  kind: "skill" | "tool";
  description: string;
};

export type CommandSource =
  | { mode: "system"; suggestions: AgentCommandSuggestion[] }
  | { mode: "claudeCode"; slashCommands: string[] }
  | { mode: "unsupported" };

export type SuggestionTrigger = {
  type: "command" | "file";
  query: string;
  start: number;
  end: number;
};

export type SuggestionReplacement = {
  value: string;
  caretIndex: number;
};

export type FileSuggestionCandidate = {
  fullPath: string;
  relativePath: string;
};

export type FileSuggestionSearch = (keyword: string) => Promise<SuggestionItem[]>;

export type AgentSuggestionsResponse = components["schemas"]["AgentSuggestionsResponse"];

export const IGNORED_COMMANDS = [
  "/add-dir",
  "/agents",
  "/config",
  "/statusline",
  "/bashes",
  "/settings",
  "/cost",
  "/doctor",
  "/exit",
  "/help",
  "/ide",
  "/init",
  "/install-github-app",
  "/mcp",
  "/memory",
  "/migrate-installer",
  "/model",
  "/pr-comments",
  "/release-notes",
  "/resume",
  "/status",
  "/bug",
  "/review",
  "/security-review",
  "/terminal-setup",
  "/upgrade",
  "/vim",
  "/permissions",
  "/hooks",
  "/export",
  "/logout",
  "/login",
];

const DEFAULT_COMMANDS: SuggestionItem[] = [
  { text: "/compact", description: "Compact the conversation history" },
  { text: "/clear", description: "Clear the conversation" },
  {
    text: "/status",
    description:
      "Show Claude Code status including version, model, account, API connectivity, and tool statuses",
  },
];

const COMMAND_DESCRIPTIONS: Record<string, string> = {
  "/compact": "Compact the conversation history",
  "/help": "Show available commands",
  "/clear": "Clear the conversation",
  "/reset": "Reset the session",
  "/export": "Export conversation",
  "/debug": "Show debug information",
  "/status": "Show connection status",
  "/stop": "Stop current operation",
  "/abort": "Abort current operation",
  "/cancel": "Cancel current operation",
};

export function getAgentSuggestionQueryParams(
  projectId: string | null,
  target: (Pick<ChatTargetOption, "id" | "type"> & Partial<Pick<ChatTargetOption, "label">>) | null,
): { projectId?: string; agentId: string } | null {
  if (!target || target.type !== "agent") {
    return null;
  }

  return projectId ? { projectId, agentId: target.id } : { agentId: target.id };
}

export function toCommandSource(
  response: AgentSuggestionsResponse | undefined,
  claudeCommands: string[],
): CommandSource {
  if (!response || response.mode === "unsupported") {
    return { mode: "unsupported" };
  }

  if (response.mode === "claudeCode") {
    return { mode: "claudeCode", slashCommands: claudeCommands };
  }

  return { mode: "system", suggestions: response.suggestions };
}

export function getSuggestionTrigger(input: string, caretIndex: number): SuggestionTrigger | null {
  if (caretIndex < 0 || caretIndex > input.length) {
    return null;
  }

  const match = /(^|\s)([/@])([^\s]*)$/.exec(input.slice(0, caretIndex));
  if (!match || match.index === undefined) {
    return null;
  }

  const prefix = match[1];
  const marker = match[2];
  return {
    type: marker === "/" ? "command" : "file",
    query: match[3],
    start: match.index + prefix.length,
    end: caretIndex,
  };
}

export function replaceSuggestion(
  input: string,
  suggestionText: string,
  caretIndex: number,
): SuggestionReplacement {
  const trigger = getSuggestionTrigger(input, caretIndex);
  if (!trigger) {
    return { value: input, caretIndex };
  }

  const suffix = input.slice(trigger.end);
  const hasWhitespaceSeparator = /^\s/.test(suffix);
  const separator = hasWhitespaceSeparator ? "" : " ";
  const value = `${input.slice(0, trigger.start)}${suggestionText}${separator}${suffix}`;
  const nextCaretIndex =
    trigger.start + suggestionText.length + (hasWhitespaceSeparator ? 1 : separator.length);

  return { value, caretIndex: nextCaretIndex };
}

export function searchCommand(keyword: string, source: CommandSource): SuggestionItem[] {
  if (source.mode === "unsupported") {
    return [];
  }

  const commands: SuggestionItem[] =
    source.mode === "system" ? [...source.suggestions] : buildClaudeCommands(source.slashCommands);

  if (!keyword) {
    return commands.slice(0, 5);
  }

  const fuse = new Fuse(commands, {
    keys: [
      { name: "text", weight: 0.7 },
      { name: "description", weight: 0.3 },
    ],
    threshold: 0.3,
    minMatchCharLength: 1,
    ignoreLocation: true,
    shouldSort: true,
  });

  return fuse.search(keyword, { limit: 5 }).map((result) => result.item);
}

export function toFileSuggestions(
  candidates: readonly FileSuggestionCandidate[],
): SuggestionItem[] {
  return candidates.slice(0, 5).map((candidate) => {
    const path = candidate.relativePath.includes(" ")
      ? `"${candidate.relativePath}"`
      : candidate.relativePath;
    return {
      text: `@${path}`,
      description: candidate.fullPath,
    };
  });
}

export function resolveInputSuggestions(
  input: string,
  caretIndex: number,
  commandSource: CommandSource,
  searchFiles?: FileSuggestionSearch,
): SuggestionItem[] | Promise<SuggestionItem[]> {
  const trigger = getSuggestionTrigger(input, caretIndex);
  if (!trigger) {
    return [];
  }

  if (trigger.type === "command") {
    return searchCommand(trigger.query, commandSource);
  }

  return searchFiles?.(trigger.query) ?? [];
}

function buildClaudeCommands(slashCommands: string[]): SuggestionItem[] {
  const commands: SuggestionItem[] = [...DEFAULT_COMMANDS];
  const commandNames = new Set(commands.map((command) => command.text.toLowerCase()));

  for (const rawCommand of slashCommands) {
    const trimmedCommand = rawCommand.trim();
    if (!trimmedCommand) {
      continue;
    }

    const command = trimmedCommand.startsWith("/") ? trimmedCommand : `/${trimmedCommand}`;
    const normalizedCommand = command.toLowerCase();
    if (IGNORED_COMMANDS.includes(normalizedCommand) || commandNames.has(normalizedCommand)) {
      continue;
    }

    commandNames.add(normalizedCommand);
    commands.push({
      text: command,
      description: COMMAND_DESCRIPTIONS[normalizedCommand],
    });
  }

  return commands;
}
