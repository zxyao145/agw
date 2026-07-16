import type { SuggestionItem } from "@/components/message/user-input";
import Fuse from "fuse.js";

export type AgentCommandSuggestion = SuggestionItem & {
  kind: "skill" | "tool";
};

export type CommandSource =
  | { mode: "system"; suggestions: AgentCommandSuggestion[] }
  | { mode: "claudeCode"; slashCommands: string[] }
  | { mode: "unsupported" };

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

export const searchCommand = (keyword: string, source: CommandSource): SuggestionItem[] => {
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
