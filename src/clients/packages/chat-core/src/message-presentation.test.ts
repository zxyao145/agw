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
  prepareConversationHistory,
} from "./message-presentation.ts";

function systemMessage(messageId: string, content: string): AiMessage {
  return {
    messageId,
    role: "system",
    contents: [{ type: "TextContent", content }],
  };
}

test("history combines adjacent fragments while preserving content order and turn boundaries", () => {
  const messages: AiMessage[] = ["first", "second"].flatMap((turn) => [
    {
      messageId: turn,
      role: "user",
      contents: [{ type: "TextContent", content: turn }],
    },
    ...["The", " ", "user"].map((content) => ({
      messageId: "assistant",
      role: "assistant",
      author: "claude-code",
      contents: [{ type: "TextReasoningContent", content }],
    })),
    {
      messageId: "assistant",
      role: "assistant",
      author: "claude-code",
      contents: [
        { type: "TextContent", content: "Checking" },
        { type: "FunctionCallContent", additionalProperties: { callId: "call", toolName: "Bash" } },
      ],
    },
  ]);
  const history = prepareConversationHistory(messages).messages;

  assert.equal(history.length, 4);
  assert.deepEqual(
    history.map((message) => message.streamingScopeId),
    ["first", "first", "second", "second"],
  );
  assert.deepEqual(
    history[1].contents.map((content) => [content.type, content.content]),
    [
      ["TextReasoningContent", "The user"],
      ["TextContent", "Checking"],
      ["FunctionCallContent", undefined],
    ],
  );
  assert.equal(messages[1].contents[0].content, "The");
  assert.deepEqual(prepareConversationHistory(history).messages, history);
});

for (const boundary of ["id", "author", "role", "result", "missing-id", "tool-result"]) {
  test(`history does not merge across a ${boundary} boundary`, () => {
    const first: AiMessage = {
      messageId: boundary === "missing-id" ? "" : "assistant",
      role: "assistant",
      author: "claude-code",
      contents: [{ type: "TextContent", content: "before" }],
    };
    const second: AiMessage = { ...first, contents: [{ type: "TextContent", content: "after" }] };
    if (boundary === "id") second.messageId = "other";
    if (boundary === "author") second.author = "other-agent";
    if (boundary === "role") second.role = "tool";
    if (boundary === "result") second.contents[0].additionalProperties = { type: "result" };
    const messages: AiMessage[] = [
      { messageId: "user", role: "user", contents: [{ type: "TextContent", content: "Review" }] },
      first,
    ];
    if (boundary === "tool-result")
      messages.push({
        messageId: "tool-result",
        role: "user",
        contents: [
          {
            type: "FunctionResultContent",
            content: "output",
            additionalProperties: { callId: "call" },
          },
        ],
      });
    messages.push(second);
    const history = prepareConversationHistory(messages).messages;

    assert.equal(history.length, messages.length);
    assert.equal(history[1].contents[0].content, "before");
    assert.equal(history.at(-1)!.contents[0].content, "after");
  });
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

test("uses AuthorName as the canonical External Agent name", () => {
  assert.deepEqual(
    getMessageMeta({
      messageId: "message-1",
      role: "assistant",
      author: "pi",
      contents: [],
      additionalProperties: {
        modelName: "deepseek-v4-flash-vision-exp",
      },
    }),
    { name: "pi", author: null, model: "deepseek-v4-flash-vision-exp" },
  );
});

test("keeps legacy External Agent history that stored the model in AuthorName", () => {
  assert.deepEqual(
    getMessageMeta({
      messageId: "message-1",
      role: "assistant",
      author: "claude-sonnet",
      contents: [],
      additionalProperties: { agentName: "claude-code" },
    }),
    { name: "claude-code", author: "claude-sonnet", model: null },
  );
});

test("renders Agentflow node, canonical agent, and model independently", () => {
  assert.deepEqual(
    getMessageMeta({
      messageId: "message-1",
      role: "assistant",
      author: "pi",
      contents: [],
      additionalProperties: {
        nodeName: "Review Node",
        modelName: "deepseek-v4-flash-vision-exp",
      },
    }),
    { name: "Review Node", author: "pi", model: "deepseek-v4-flash-vision-exp" },
  );
});

test("prefers an explicit author over the persisted Agentflow agent name", () => {
  assert.deepEqual(
    getMessageMeta({
      messageId: "message-1",
      role: "assistant",
      author: "live-agent",
      contents: [],
      additionalProperties: { nodeName: "Review Node", agentName: "historical-agent" },
    }),
    { name: "Review Node", author: "live-agent", model: null },
  );
});

test("uses the persisted agent name when historical Agentflow messages have no author", () => {
  assert.deepEqual(
    getMessageMeta({
      messageId: "message-1",
      role: "assistant",
      author: null,
      contents: [],
      additionalProperties: { nodeName: "Review Node", agentName: "general-agent" },
    }),
    { name: "Review Node", author: "general-agent", model: null },
  );
});

test("does not duplicate a standalone agent name", () => {
  assert.deepEqual(
    getMessageMeta({
      messageId: "message-1",
      role: "assistant",
      author: null,
      contents: [],
      additionalProperties: { agentName: "general-agent" },
    }),
    { name: "general-agent", author: null, model: null },
  );
});

test("does not duplicate matching agent name and author", () => {
  assert.deepEqual(
    getMessageMeta({
      messageId: "message-1",
      role: "assistant",
      author: "PI",
      contents: [],
      additionalProperties: { agentName: "pi" },
    }),
    { name: "PI", author: null, model: null },
  );
});

test("does not add historical agent metadata to tool messages", () => {
  assert.deepEqual(
    getMessageMeta({
      messageId: "message-1",
      role: "tool",
      author: null,
      contents: [],
      additionalProperties: { nodeName: "Review Node", agentName: "general-agent" },
    }),
    { name: "Review Node", author: null, model: null },
  );
});

test("uses the first line for collapsed message previews", () => {
  assert.equal(getMessagePreview("Planning the change\nMore detail"), "Planning the change");
  assert.match(getMessagePreview("reasoning ".repeat(40).trim()), /…$/);
});
