import assert from "node:assert/strict";
import test from "node:test";

import type { AiMessage } from "@agw/api";
import { scopeMessagesByUserTurn } from "@agw/execution-core";
import {
  buildConversationRenderModel,
  formatToolContent,
  formatToolResultContent,
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

function claudeSystemMessage(
  messageId: string,
  content: string,
  additionalProperties: Record<string, unknown> = {},
  contentType = "TextContent",
): AiMessage {
  return {
    messageId,
    role: "system",
    streamingScopeId: "user-1",
    contents: [{ type: contentType, content }],
    additionalProperties: { agentName: "claude-code", ...additionalProperties },
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

test("visible messages keep only the latest Claude API retry in each consecutive run", () => {
  const retryMessage = (attempt: number) =>
    claudeSystemMessage(
      `api-retry-${attempt}`,
      `Claude Code API retry ${attempt}/10`,
      { subtype: "api_retry" },
      "ErrorContent",
    );
  const sessionStart = claudeSystemMessage(
    "session-start",
    JSON.stringify({ type: "system", hook_event: "SessionStart" }),
  );
  const firstRetry = retryMessage(1);
  const secondRetry = retryMessage(2);
  const assistant = message("assistant", "assistant", "Working again", {
    agentName: "claude-code",
  });
  const thirdRetry = retryMessage(3);
  const fourthRetry = retryMessage(4);

  assert.deepEqual(
    prepareVisibleMessages([sessionStart, firstRetry, secondRetry]).map((item) => item.messageId),
    ["session-start", "api-retry-2"],
  );
  assert.deepEqual(
    prepareVisibleMessages([
      sessionStart,
      firstRetry,
      secondRetry,
      assistant,
      thirdRetry,
      fourthRetry,
    ]).map((item) => item.messageId),
    ["session-start", "api-retry-2", "assistant", "api-retry-4"],
  );

  const rendered = buildConversationRenderModel([sessionStart, firstRetry, secondRetry]);
  const latestRetry = rendered.find(
    (item) => item.type === "message" && item.message.source.messageId === "api-retry-2",
  );
  assert.deepEqual(latestRetry?.type === "message" ? latestRetry.message.contents : [], [
    { type: "error", text: "Claude Code API retry 2/10" },
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

test("consecutive tool calls in one turn become a compact batch with summaries", () => {
  const toolPair = (
    callId: string,
    toolName: string,
    argumentsValue: Record<string, unknown>,
    resultValue: unknown,
  ): AiMessage[] => [
    {
      messageId: `${callId}-call`,
      role: "assistant",
      author: "agent",
      streamingScopeId: "user-1",
      contents: [
        {
          type: "FunctionCallContent",
          content: JSON.stringify(argumentsValue),
          additionalProperties: { callId, toolName },
        },
      ],
    },
    {
      messageId: `${callId}-result`,
      role: "tool",
      author: "agent",
      streamingScopeId: "user-1",
      contents: [
        {
          type: "FunctionResultContent",
          content: JSON.stringify(resultValue),
          additionalProperties: { callId },
        },
      ],
    },
  ];

  const messages = [
    ...toolPair("bash-1", "Bash", { description: "  Run\n  tests  " }, "done"),
    ...toolPair("read-1", "Read", { file_path: "src/file.ts" }, { ok: true }),
  ];
  const unbatchedItems = buildConversationRenderModel(messages);
  assert.deepEqual(
    unbatchedItems.map((item) => item.type),
    ["tool-accordion", "tool-accordion"],
  );

  const items = buildConversationRenderModel(messages, { collapseToolRuns: true });

  assert.equal(items.length, 1);
  const batch = items[0];
  assert.equal(batch?.type, "tool-batch");
  if (batch?.type !== "tool-batch") return;

  assert.equal(batch.tools.length, 2);
  assert.deepEqual(
    batch.tools.map((tool) => [tool.toolName, tool.summary, tool.status]),
    [
      ["Bash", "Run tests", "complete"],
      ["Read", "src/file.ts", "complete"],
    ],
  );
});

test("tool batches pair out-of-order results before preserving tool-use order", () => {
  const calls: AiMessage = {
    messageId: "concurrent-calls",
    role: "assistant",
    author: "agent",
    streamingScopeId: "user-1",
    contents: [
      {
        type: "FunctionCallContent",
        content: JSON.stringify({ file_path: "src/file.ts" }),
        additionalProperties: { callId: "call-1", toolName: "Read" },
      },
      {
        type: "FunctionCallContent",
        content: JSON.stringify({ command: "pnpm test" }),
        additionalProperties: { callId: "call-2", toolName: "Bash" },
      },
      {
        type: "FunctionCallContent",
        content: JSON.stringify({ path: "src/file.ts" }),
        additionalProperties: { callId: "call-3", toolName: "Edit" },
      },
    ],
  };
  const result = (callId: string): AiMessage => ({
    messageId: `${callId}-result`,
    role: "tool",
    author: "agent",
    streamingScopeId: "replayed-scope",
    contents: [
      {
        type: "FunctionResultContent",
        content: JSON.stringify({ ok: true }),
        additionalProperties: { callId },
      },
    ],
  });

  const items = buildConversationRenderModel(
    [calls, result("call-3"), result("call-1"), result("call-2")],
    { collapseToolRuns: true },
  );

  assert.equal(items.length, 1);
  const batch = items[0];
  assert.equal(batch?.type, "tool-batch");
  if (batch?.type !== "tool-batch") return;

  assert.deepEqual(
    batch.tools.map((tool) => tool.toolName),
    ["Read", "Bash", "Edit"],
  );
  assert.deepEqual(
    batch.tools.map((tool) =>
      tool.messages.flatMap((message) =>
        message.source.contents.map((content) => content.additionalProperties?.callId),
      ),
    ),
    [
      ["call-1", "call-1"],
      ["call-2", "call-2"],
      ["call-3", "call-3"],
    ],
  );
  assert.deepEqual(
    batch.tools.map((tool) => tool.status),
    ["complete", "complete", "complete"],
  );
});

test("pending and failed tool calls expose stable identities and status", () => {
  const call: AiMessage = {
    messageId: "pending-call",
    role: "assistant",
    author: "agent",
    streamingScopeId: "user-1",
    contents: [
      {
        type: "FunctionCallContent",
        content: JSON.stringify({ command: "pnpm test" }),
        additionalProperties: { callId: "call-1", toolName: "Bash" },
      },
    ],
  };
  const pending = buildConversationRenderModel([call], { collapseToolRuns: true });
  const pendingTool = pending[0];
  assert.equal(pendingTool?.type, "tool-accordion");
  if (pendingTool?.type !== "tool-accordion") return;
  assert.equal(pendingTool.status, "running");
  assert.equal(pendingTool.summary, "pnpm test");

  const unbatchedPending = buildConversationRenderModel([call]);
  assert.equal(unbatchedPending[0]?.type, "message");

  const failed = buildConversationRenderModel([
    call,
    {
      messageId: "failed-result",
      role: "tool",
      author: "agent",
      streamingScopeId: "user-1",
      contents: [
        {
          type: "FunctionResultContent",
          content: JSON.stringify({ isError: true, message: "failed" }),
          additionalProperties: { callId: "call-1" },
        },
      ],
    },
  ]);
  const failedTool = failed[0];
  assert.equal(failedTool?.type, "tool-accordion");
  if (failedTool?.type !== "tool-accordion") return;
  assert.equal(failedTool.status, "failed");
  assert.equal(failedTool.identity, pendingTool.identity);
});

test("tool batches stop at ordinary messages and scope boundaries", () => {
  const tool = (id: string, scope: string): AiMessage[] => [
    {
      messageId: `${id}-call`,
      role: "assistant",
      author: "agent",
      streamingScopeId: scope,
      contents: [
        {
          type: "FunctionCallContent",
          content: JSON.stringify({ command: id }),
          additionalProperties: { callId: `${id}-call`, toolName: "Bash" },
        },
      ],
    },
    {
      messageId: `${id}-result`,
      role: "tool",
      author: "agent",
      streamingScopeId: scope,
      contents: [
        {
          type: "FunctionResultContent",
          content: "done",
          additionalProperties: { callId: `${id}-call` },
        },
      ],
    },
  ];

  const separatedByText = buildConversationRenderModel(
    [
      ...tool("first", "user-1"),
      message("text", "assistant", "between tools"),
      ...tool("second", "user-1"),
    ],
    { collapseToolRuns: true },
  );
  assert.deepEqual(
    separatedByText.map((item) => item.type),
    ["tool-accordion", "message", "tool-accordion"],
  );

  const separatedByScope = buildConversationRenderModel(
    [...tool("first", "user-1"), ...tool("second", "user-2")],
    { collapseToolRuns: true },
  );
  assert.deepEqual(
    separatedByScope.map((item) => item.type),
    ["tool-accordion", "tool-accordion"],
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

test("tool result formatting fences plain text and keeps JSON blocks", () => {
  assert.equal(formatToolResultContent("line 1\nline 2"), "\n```\nline 1\nline 2\n```");
  assert.match(formatToolResultContent('{"a":1}'), /^\n```json\n/);
  assert.equal(formatToolResultContent(""), "");
  assert.equal(
    formatToolResultContent("```ts\nconst x = 1;\n```"),
    "\n````\n```ts\nconst x = 1;\n```\n````",
  );
});

test("plain text tool results render as fenced code in presented messages", () => {
  const items = buildConversationRenderModel([
    {
      messageId: "shell-call",
      role: "assistant",
      streamingScopeId: "user-1",
      contents: [
        {
          type: "FunctionCallContent",
          content: "plain args",
          additionalProperties: { callId: "shell-1", toolName: "run_shell" },
        },
      ],
    },
    {
      messageId: "shell-result",
      role: "tool",
      streamingScopeId: "user-1",
      contents: [
        {
          type: "FunctionResultContent",
          content: "i tests 151\ni pass 151",
          additionalProperties: { callId: "shell-1" },
        },
      ],
    },
  ]);

  const tool = items.find((item) => item.type === "tool-accordion");
  const markdowns =
    tool?.type === "tool-accordion"
      ? tool.messages.flatMap((toolMessage) =>
          toolMessage.contents.map((content) =>
            content.type === "markdown" ? content.markdown : "",
          ),
        )
      : [];
  assert.deepEqual(markdowns, ["plain args", "\n```\ni tests 151\ni pass 151\n```"]);
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
