import assert from "node:assert/strict";
import test from "node:test";

import type { AiMessage } from "@agw/api";
import { scopeMessagesByUserTurn } from "@agw/execution-core";
import {
  buildConversationRenderModel,
  formatToolContent,
  isSupportedImageDataUrl,
  prepareVisibleMessages,
} from "./conversation-render-model";

function message(
  messageId: string,
  role: string,
  content: string,
  additionalProperties?: Record<string, unknown>,
): AiMessage {
  return {
    messageId,
    role,
    author: role === "user" ? undefined : "agent",
    streamingScopeId: "user-1",
    contents: [{ type: "TextContent", content }],
    additionalProperties,
  };
}

test("visible messages remove usage and controls before collapsing ordinary system runs", () => {
  const visible = prepareVisibleMessages([
    message("system-1", "system", "first"),
    message("start", "system", "", { type: "turn-start" }),
    message("system-2", "system", "latest"),
    {
      ...message("usage", "assistant", "visible"),
      contents: [
        { type: "TextContent", content: "visible" },
        { type: "UsageContent", content: { totalTokenCount: 3 } },
      ],
    },
  ]);

  assert.deepEqual(
    visible.map((item) => [item.messageId, item.contents.map((content) => content.type)]),
    [
      ["system-2", ["TextContent"]],
      ["usage", ["TextContent"]],
    ],
  );
});

test("visible messages keep Claude SessionStart and results while hiding other Claude system messages", () => {
  const claudeSystemMessage = (
    messageId: string,
    content: string,
    additionalProperties: Record<string, unknown> = {},
    contentType = "TextContent",
  ): AiMessage => ({
    messageId,
    role: "system",
    streamingScopeId: "user-1",
    contents: [{ type: contentType, content }],
    additionalProperties: { agentName: "claude-code", ...additionalProperties },
  });
  const sessionStart = claudeSystemMessage(
    "session-start",
    JSON.stringify({
      type: "system",
      hook_id: "hook-1",
      hook_event: "SessionStart",
    }),
  );
  const taskProgress = claudeSystemMessage(
    "task-progress",
    JSON.stringify({
      type: "system",
      tool_use_id: "Agent_4",
      description: "Reading files",
    }),
  );
  const otherHook = claudeSystemMessage(
    "other-hook",
    JSON.stringify({ type: "system", hook_event: "PreToolUse" }),
  );
  const apiRetry = claudeSystemMessage(
    "api-retry",
    "Claude Code API retry 1/3",
    { subtype: "api_retry" },
    "ErrorContent",
  );
  const successResult = claudeSystemMessage("success-result", "done", { type: "result" });
  const errorResult = claudeSystemMessage(
    "error-result",
    "failed",
    { type: "result" },
    "ErrorContent",
  );
  const agwSystem = message("agw-system", "system", "keep server status", {
    agentName: "Agw",
  });

  const visible = prepareVisibleMessages([
    sessionStart,
    taskProgress,
    otherHook,
    apiRetry,
    successResult,
    errorResult,
    agwSystem,
  ]);

  assert.deepEqual(
    visible.map((item) => item.messageId),
    ["session-start", "success-result", "error-result", "agw-system"],
  );

  const rendered = buildConversationRenderModel([sessionStart]);
  assert.deepEqual(rendered[0]?.type === "message" ? rendered[0].message.contents : [], [
    { type: "plain", text: "SessionStart", sourceType: "TextContent" },
  ]);
});

test("visible messages keep the legacy nested Claude SessionStart before hiding later progress", () => {
  const sessionStart: AiMessage = {
    messageId: "legacy-session-start",
    role: "system",
    streamingScopeId: "user-1",
    contents: [
      {
        type: "TextContent",
        content: JSON.stringify({
          output: JSON.stringify({
            hookSpecificOutput: JSON.stringify({ hookEventName: "SessionStart" }),
          }),
        }),
      },
    ],
    additionalProperties: { agentName: "CLAUDE-CODE" },
  };
  const taskProgress: AiMessage = {
    ...sessionStart,
    messageId: "task-progress",
    contents: [
      {
        type: "TextContent",
        content: JSON.stringify({ type: "system", tool_use_id: "Agent_4" }),
      },
    ],
  };

  assert.deepEqual(
    prepareVisibleMessages([sessionStart, taskProgress]).map((item) => item.messageId),
    ["legacy-session-start"],
  );
});

test("visible messages do not infer Claude source without the agent marker", () => {
  const unmarkedSystem = message(
    "unmarked-system",
    "system",
    JSON.stringify({ type: "system", tool_use_id: "Agent_4" }),
  );
  const claudeAssistant = message("claude-assistant", "assistant", "keep response", {
    agentName: "claude-code",
  });

  assert.deepEqual(
    prepareVisibleMessages([unmarkedSystem, claudeAssistant]).map((item) => item.messageId),
    ["unmarked-system", "claude-assistant"],
  );
});

test("visible messages hide system-injected AI context without matching its text", () => {
  const visible = prepareVisibleMessages([
    message("todo-context", "user", "Current todo list", {
      _attribution: {
        sourceType: { value: "AIContextProvider" },
        sourceId: "TodoProvider",
      },
    }),
    message("memory-context", "user", "arbitrary private context", {
      _attribution: "AIContextProvider:UserMemoryProvider",
    }),
    message("real-user", "user", "keep me", {
      _attribution: { sourceType: { value: "External" }, sourceId: null },
    }),
  ]);

  assert.deepEqual(
    visible.map((item) => item.messageId),
    ["real-user"],
  );
});

test("render model hides Skill loaders and their model-excluded display sidecars", () => {
  const items = buildConversationRenderModel([
    message("user", "user", "review this"),
    {
      messageId: "skill-call",
      role: "assistant",
      author: "agent",
      streamingScopeId: "user-1",
      contents: [
        {
          type: "FunctionCallContent",
          content: '{"skill":"commit"}',
          additionalProperties: { callId: "skill-1", toolName: "Skill" },
        },
      ],
    },
    {
      messageId: "skill-result",
      role: "user",
      streamingScopeId: "user-1",
      additionalProperties: { modelHistoryExcluded: true },
      contents: [
        {
          type: "FunctionResultContent",
          content: '{"success":true}',
          additionalProperties: { callId: "skill-1" },
        },
      ],
    },
    {
      ...message("skill-sidecar", "user", "internal skill instructions"),
      additionalProperties: { modelHistoryExcluded: true },
    },
    message("assistant", "assistant", "review complete"),
  ]);

  assert.deepEqual(
    items.map((item) => (item.type === "message" ? item.message.source.messageId : item.type)),
    ["user", "assistant"],
  );
});

test("render model hides shared skill loader tools but keeps ordinary tool accordions", () => {
  const toolPair = (toolName: string, callId: string): AiMessage[] => [
    {
      messageId: `${callId}-call`,
      role: "assistant",
      streamingScopeId: "user-1",
      contents: [
        {
          type: "FunctionCallContent",
          content: "{}",
          additionalProperties: { callId, toolName },
        },
      ],
    },
    {
      messageId: `${callId}-result`,
      role: "tool",
      streamingScopeId: "user-1",
      contents: [
        {
          type: "FunctionResultContent",
          content: "done",
          additionalProperties: { callId },
        },
      ],
    },
  ];

  const items = buildConversationRenderModel([
    ...toolPair("load_skill", "load"),
    ...toolPair("read_skill_resource", "read"),
    ...toolPair("command_execution", "command"),
  ]);

  assert.deepEqual(
    items.map((item) => (item.type === "tool-accordion" ? item.toolName : item.type)),
    ["command_execution"],
  );
});

test("render model emits plan, full result, right user, image, and red error semantics", () => {
  const items = buildConversationRenderModel([
    message("user-1", "user", "hello"),
    message("plan", "assistant", "<proposed_plan>\n# Plan\n</proposed_plan>"),
    {
      ...message("result", "assistant", "done", { type: "result" }),
      additionalProperties: { type: "result", nodeName: "hidden" },
    },
    {
      messageId: "media",
      role: "assistant",
      author: "agent",
      streamingScopeId: "user-1",
      contents: [
        { type: "DataContent", uri: "data:image/png;base64,AQ==", name: "one.png" },
        { type: "DataContent", uri: "data:image/svg+xml;base64,AQ==" },
        { type: "ErrorContent", content: "broken" },
      ],
    },
  ]);

  assert.deepEqual(
    items.map((item) => [item.type, item.alignment, item.width]),
    [
      ["message", "right", "normal"],
      ["plan", "left", "full"],
      ["result", "left", "full"],
      ["message", "left", "normal"],
    ],
  );
  const result = items[2];
  assert.equal(result.type === "result" ? result.message.meta : "unexpected", null);
  const media = items[3];
  assert.deepEqual(
    media.type === "message" ? media.message.contents.map((content) => content.type) : [],
    ["image", "error"],
  );
});

test("tool calls pair per scope and completed questions use a dedicated result item", () => {
  const call: AiMessage = {
    messageId: "call",
    role: "assistant",
    author: "agent",
    streamingScopeId: "user-1",
    contents: [
      {
        type: "FunctionCallContent",
        content: { questions: [] },
        additionalProperties: { callId: "call-1", toolName: "ask_user_question" },
      },
    ],
  };
  const result: AiMessage = {
    messageId: "result",
    role: "tool",
    author: "agent",
    streamingScopeId: "user-1",
    contents: [
      {
        type: "FunctionResultContent",
        content: JSON.stringify({
          questions: [{ question: "Choose" }],
          answers: { Choose: "A" },
        }),
        additionalProperties: { callId: "call-1" },
      },
    ],
  };

  assert.deepEqual(
    buildConversationRenderModel([call, result]).map((item) => item.type),
    ["human-interaction-result"],
  );
});

test("Claude AskUserQuestion calls use the dedicated result item and support array answers", () => {
  const call: AiMessage = {
    messageId: "claude-call",
    role: "assistant",
    author: "claude",
    streamingScopeId: "user-claude",
    contents: [
      {
        type: "FunctionCallContent",
        content: { questions: [] },
        additionalProperties: { callId: "call-claude", toolName: "AskUserQuestion" },
      },
    ],
  };
  const result: AiMessage = {
    messageId: "claude-result",
    role: "tool",
    author: "claude",
    streamingScopeId: "user-claude",
    contents: [
      {
        type: "FunctionResultContent",
        content: JSON.stringify({
          questions: [{ question: "Choose sections" }],
          answers: { "Choose sections": ["Intro", "Conclusion"] },
        }),
        additionalProperties: { callId: "call-claude" },
      },
    ],
  };

  const items = buildConversationRenderModel([call, result]);

  assert.deepEqual(
    items.map((item) => item.type),
    ["human-interaction-result"],
  );
  const interaction = items[0];
  assert.equal(
    interaction.type === "human-interaction-result" ? interaction.result.items[0]?.answer : null,
    "Intro, Conclusion",
  );
});

test("Claude pseudo-user tool results pair with their assistant calls after history scoping", () => {
  const messages = scopeMessagesByUserTurn([
    {
      messageId: "user-1",
      role: "user",
      author: "$agw",
      contents: [{ type: "TextContent", content: "format" }],
    },
    {
      messageId: "call-message",
      role: "assistant",
      author: "kimi-k3",
      contents: [
        {
          type: "FunctionCallContent",
          content: '{"command":"pnpm fmt"}',
          additionalProperties: { callId: "Bash_0", toolName: "Bash" },
        },
      ],
    },
    {
      messageId: "result-message",
      role: "user",
      additionalProperties: { modelHistoryExcluded: true },
      contents: [
        {
          type: "FunctionResultContent",
          content: '{"stdout":"done"}',
          additionalProperties: { callId: "Bash_0" },
        },
      ],
    },
  ]);

  const items = buildConversationRenderModel(messages);
  assert.deepEqual(
    items.map((item) => item.type),
    ["message", "tool-accordion"],
  );
  const tool = items[1];
  assert.equal(tool.type === "tool-accordion" ? tool.messages.length : 0, 2);
});

test("tool formatting wraps only object and array JSON", () => {
  assert.match(formatToolContent('{"a":1}'), /^\n```json\n/);
  assert.match(formatToolContent([1, 2]), /^\n```json\n/);
  assert.equal(formatToolContent('"plain"'), '"plain"');
  assert.equal(formatToolContent(""), "");
  assert.equal(formatToolContent("invalid"), "invalid");
  assert.equal(isSupportedImageDataUrl("data:image/webp;base64,AQ=="), true);
  assert.equal(isSupportedImageDataUrl("data:image/svg+xml;base64,AQ=="), false);
});

test("tool state snapshots remain dedicated render items even without visible contents", () => {
  const items = buildConversationRenderModel([
    {
      messageId: "todo-1",
      role: "system",
      contents: [],
      additionalProperties: { type: "tool-todo-snapshot", items: [] },
    },
  ]);
  assert.deepEqual(
    items.map((item) => item.type),
    ["tool-state"],
  );
});
