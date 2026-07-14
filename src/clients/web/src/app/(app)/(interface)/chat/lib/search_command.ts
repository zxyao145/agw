// https://github.com/slopus/happy/blob/main/expo-app/sources/sync/suggestionCommands.ts

import type { SuggestionItem } from "@/components/message/user-input";
import Fuse from "fuse.js";

export type AgentCommandSuggestion = SuggestionItem & {
  kind: "skill" | "tool";
};

export type CommandSource =
  | { mode: "system"; suggestions: AgentCommandSuggestion[] }
  | { mode: "claudeCode"; slashCommands: string[] }
  | { mode: "unsupported" };

// Commands to ignore/filter out
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

// Default commands always available
const DEFAULT_COMMANDS: SuggestionItem[] = [
  { text: "/compact", description: "Compact the conversation history" },
  { text: "/clear", description: "Clear the conversation" },
  {
    text: "/status",
    description:
      "Show Claude Code status including version, model, account, API connectivity, and tool statuses",
  },
];

// Command descriptions for known tools/commands
const COMMAND_DESCRIPTIONS: Record<string, string> = {
  // Default commands
  "/compact": "Compact the conversation history",

  // Common tool commands
  "/help": "Show available commands",
  "/clear": "Clear the conversation",
  "/reset": "Reset the session",
  "/export": "Export conversation",
  "/debug": "Show debug information",
  "/status": "Show connection status",
  "/stop": "Stop current operation",
  "/abort": "Abort current operation",
  "/cancel": "Cancel current operation",

  // Add more descriptions as needed
};

export const searchCommand = (keyword: string, source: CommandSource): SuggestionItem[] => {
  if (source.mode === "unsupported") {
    return [];
  }

  const commands: SuggestionItem[] =
    source.mode === "system" ? [...source.suggestions] : buildClaudeCommands(source.slashCommands);

  if (!keyword) {
    return commands.slice(0, 5);
  }

  // Use Fuse.js for fuzzy search
  const fuse = new Fuse(commands, {
    keys: [
      { name: "text", weight: 0.7 },
      { name: "description", weight: 0.3 },
    ],
    threshold: 0.3, // Lower = more exact match, 0.3 is a good balance
    minMatchCharLength: 1,
    ignoreLocation: true,
    shouldSort: true,
  });

  // Search and limit results to 5
  const results = fuse.search(keyword, { limit: 5 });
  const suggestions = results.map((result) => result.item);

  return suggestions;
};

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
