import assert from "node:assert/strict";
import test from "node:test";

import type { AiMessage } from "@agw/api";
import {
  collapseConsecutiveSystemMessages,
  formatSystemMessageContent,
  getClaudeHookEventName,
  getClaudeSystemEventName,
  getMessageMeta,
  getMessagePreview,
  prepareClaudeHistory,
} from "./message-presentation.ts";

function systemMessage(messageId: string, content: string): AiMessage {
  return {
    messageId,
    role: "system",
    contents: [{ type: "TextContent", content }],
  };
}

test("collapses consecutive system messages with the same rules as Desktop", () => {
  const first = systemMessage("first", "first");
  const latest = systemMessage("latest", "latest");
  const turnStarted: AiMessage = {
    ...systemMessage("turn-started", "turn.started"),
    additionalProperties: { type: "turn.started" },
  };

  assert.deepEqual(collapseConsecutiveSystemMessages([first, latest, turnStarted]), [
    latest,
    turnStarted,
  ]);
});

test("system result cards form a boundary between ordinary system runs", () => {
  const first = systemMessage("first", "first");
  const result: AiMessage = {
    ...systemMessage("result", "done"),
    additionalProperties: { type: "result" },
  };
  const latest = systemMessage("latest", "latest");

  assert.deepEqual(collapseConsecutiveSystemMessages([first, result, latest]), [
    first,
    result,
    latest,
  ]);
});

test("formats a Claude Code hook event without exposing its JSON envelope", () => {
  const content = JSON.stringify({
    type: "system",
    hook_id: "hook-1",
    hook_name: "SessionStart:startup",
    hook_event: "SessionStart",
    session_id: "session-1",
  });

  assert.equal(formatSystemMessageContent(content), "SessionStart");
  assert.equal(getClaudeHookEventName(JSON.stringify(content)), "SessionStart");
  assert.equal(getClaudeSystemEventName(content), "SessionStart");
});

test("extracts a hook event from concatenated historical JSON contents", () => {
  const first = JSON.stringify({
    type: "system",
    hook_id: "hook-1",
    hook_event: "SessionStart",
  });
  const second = JSON.stringify({
    type: "system",
    hook_id: "hook-2",
    hook_event: "SessionStart",
  });

  const content = `${first}${second}`;
  assert.equal(getClaudeHookEventName(content), "SessionStart");
  assert.equal(getClaudeSystemEventName(content), "SessionStart");
});

test("history removes Claude init metadata before system messages are collapsed", () => {
  const hook = systemMessage(
    "hook",
    JSON.stringify({ type: "system", hook_event: "SessionStart" }),
  );
  const init: AiMessage = {
    ...systemMessage("init", JSON.stringify({ slash_commands: ["compact"] })),
    additionalProperties: { subtype: "init" },
  };

  const history = prepareClaudeHistory([hook, init]);

  assert.deepEqual(history, { messages: [hook], commands: ["compact"] });
  assert.deepEqual(collapseConsecutiveSystemMessages(history.messages), [hook]);
  assert.deepEqual(collapseConsecutiveSystemMessages([hook, init]), [hook]);
});

test("history removes AI context provider messages before turn scoping", () => {
  const injected: AiMessage = {
    messageId: "tool-block-context",
    role: "user",
    contents: [{ type: "TextContent", content: "internal context" }],
    additionalProperties: {
      _attribution: {
        sourceType: { value: "AIContextProvider" },
        sourceId: "ProjectMemoryProvider",
      },
    },
  };
  const user: AiMessage = {
    messageId: "user",
    role: "user",
    contents: [{ type: "TextContent", content: "visible" }],
  };
  const skillSidecar: AiMessage = {
    messageId: "skill-sidecar",
    role: "user",
    contents: [{ type: "TextContent", content: "internal skill contents" }],
    additionalProperties: { modelHistoryExcluded: true },
  };
  const toolResult: AiMessage = {
    messageId: "tool-result",
    role: "user",
    contents: [
      {
        type: "FunctionResultContent",
        content: "result",
        additionalProperties: { callId: "call-1" },
      },
    ],
    additionalProperties: { modelHistoryExcluded: true },
  };

  assert.deepEqual(prepareClaudeHistory([injected, skillSidecar, toolResult, user]), {
    messages: [toolResult, user],
    commands: [],
  });
});

test("keeps the Desktop nested hook output format", () => {
  const content = JSON.stringify({
    output: JSON.stringify({
      hookSpecificOutput: JSON.stringify({ hookEventName: "SessionStart" }),
    }),
  });

  assert.equal(formatSystemMessageContent(content), "SessionStart");
  assert.equal(getClaudeSystemEventName(content), "SessionStart");
});

test("restores Desktop agent name and author metadata", () => {
  assert.deepEqual(
    getMessageMeta({
      messageId: "message-1",
      role: "assistant",
      author: "kimi-k3",
      contents: [],
      additionalProperties: { agentName: "claude-code" },
    }),
    { name: "claude-code", author: "kimi-k3" },
  );
});

test("uses the first line for collapsed message previews", () => {
  assert.equal(getMessagePreview("Planning the change\nMore detail"), "Planning the change");
  assert.match(getMessagePreview("reasoning ".repeat(40).trim()), /…$/);
});
