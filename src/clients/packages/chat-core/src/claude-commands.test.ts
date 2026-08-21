import assert from "node:assert/strict";
import test from "node:test";

import type { AiMessage } from "@agw/api";
import {
  getClaudeHistoryCommands,
  getClaudeInitCommands,
  parseClaudeInitCommands,
} from "./claude-commands.ts";

function initMessage(content: unknown): AiMessage {
  return {
    messageId: "init",
    role: "system",
    contents: [{ type: "TextContent", content }],
    additionalProperties: { subtype: "init" },
  } as AiMessage;
}

test("extracts valid Claude slash commands and ignores non-string values", () => {
  const message = initMessage(JSON.stringify({ slash_commands: ["compact", "/review", 42] }));
  assert.deepEqual(getClaudeInitCommands(message), ["compact", "/review"]);
  assert.deepEqual(parseClaudeInitCommands(message), {
    isInit: true,
    isValid: true,
    commands: ["compact", "/review"],
  });
});

test("history restores the latest valid Claude commands without letting malformed init win", () => {
  const messages = [
    initMessage(JSON.stringify({ slash_commands: ["old"] })),
    initMessage(JSON.stringify({ slash_commands: ["new"] })),
    initMessage("{bad json"),
  ];

  assert.deepEqual(getClaudeHistoryCommands(messages), ["new"]);
  assert.deepEqual(getClaudeInitCommands(messages[2]), []);
});
