import assert from "node:assert/strict";
import test from "node:test";

import type { AiMessage } from "@agw/api";
import { scopeMessagesByUserTurn } from "@agw/execution-core";
import {
  buildConversationRenderModel,
  formatToolContent,
  formatToolResultContent,
  getCurrentTurnTodoItems,
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

test("Claude intermediate results stay ordinary messages live and after history replay", () => {
  const progress = message("progress", "assistant", "两个后台 Agent 已启动，正在等待结果。", {
    type: "assistant",
    isIntermediateResult: true,
    agentName: "claude-code",
  });
  const final = message("final", "assistant", "两个后台 Agent 均已完成。", {
    type: "result",
    isIntermediateResult: false,
    agentName: "claude-code",
  });

  for (const messages of [[progress, final], JSON.parse(JSON.stringify([progress, final]))]) {
    const items = buildConversationRenderModel(messages);
    assert.deepEqual(
      items.map((item) => item.type),
      ["message", "result"],
    );
    const progressItem = items[0];
    assert.equal(progressItem?.type, "message");
    if (progressItem?.type !== "message") throw new Error("Expected a progress message");
    assert.equal(progressItem.width, "normal");
    assert.equal(progressItem.message.source.contents[0]?.content, progress.contents[0]?.content);
  }
});

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
  const grouped = buildConversationRenderModel([
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

  const items = grouped.flatMap((item) => (item.type === "work-summary" ? item.items : [item]));
  assert.deepEqual(
    items.map((item) => [item.type, item.alignment, item.width]),
    [
      ["message", "right", "normal"],
      ["plan", "left", "full"],
      ["message", "left", "normal"],
      ["result", "left", "full"],
    ],
  );
  const result = items[3];
  assert.equal(result.type === "result" ? result.message.meta : "unexpected", null);
  const media = items[2];
  assert.deepEqual(
    media.type === "message" ? media.message.contents.map((content) => content.type) : [],
    ["image", "error"],
  );
});

const schemaOptions = {
  activeAgentId: "schema-agent",
  agentResultFormats: [{ id: "schema-agent", resultFormat: "json" }],
} as const;

test("JSON results render directly without Markdown or changing source tokens", () => {
  const json = '{"value":"```<tag>"}';
  const source = {
    ...message("result-json", "assistant", json, { type: "result", resultFormat: "json" }),
    additionalProperties: { type: "result", resultFormat: "json" },
  };

  const items = buildConversationRenderModel([source], schemaOptions);

  assert.equal(items.length, 1);
  const item = items[0]!;
  assert.equal(item.type, "result");
  assert.deepEqual(item.type === "result" ? item.message.contents : [], [
    { type: "json", text: '{\n  "value": "```<tag>"\n}' },
  ]);
  assert.equal(source.contents[0]?.content, json);
});

test("historical JSON results strip model fences and trailing summaries in presentation", () => {
  const json = '[{"approved":false,"id":9007199254740993}]';
  const source = message("legacy-result", "assistant", `\`\`\`json\n${json}\n\`\`\`\n小结：完成。`);
  source.additionalProperties = { type: "result", resultFormat: "json" };
  const originalText = source.contents[0]?.content;

  const items = buildConversationRenderModel([source], schemaOptions);

  assert.equal(items.length, 1);
  const item = items[0]!;
  assert.deepEqual(item.type === "result" ? item.message.contents : [], [
    { type: "json", text: '[\n  {\n    "approved": false,\n    "id": 9007199254740993\n  }\n]' },
  ]);
  assert.equal(source.contents[0]?.content, originalText);
});

test("JSON result formatting preserves strings, number tokens, key order and empty containers", () => {
  const expected = `{
  "10": 1.00,
  "2": 1e+30,
  "nested": [
    {},
    [],
    {
      "text": "中文 ,:{}[] **literal** \\"quote\\" \\\\ \\n",
      "values": [
        true,
        null,
        -0
      ]
    }
  ]
}`;
  for (const input of [expected.replace(/\n\s*/g, ""), expected]) {
    const source = message("result", "assistant", input, { type: "result", resultFormat: "json" });
    const item = buildConversationRenderModel([source], schemaOptions)[0]!;
    assert.deepEqual(item.type === "result" ? item.message.contents : [], [
      { type: "json", text: expected },
    ]);
    assert.equal(source.contents[0]?.content, input);
  }
});

test("all Agent kinds format JSON Results with a configured schema, regardless of SDK markers", () => {
  const json = '{"approved":false,"id":9007199254740993}';
  for (const author of ["system-agent", "claude-code", "codex", "pi", "future-agent"]) {
    for (const contentMetadata of [false, true]) {
      const source = message("claude-result", "assistant", json, {
        type: contentMetadata ? "assistant" : "result",
        subtype: "success",
      });
      source.author = author;
      if (contentMetadata) source.contents[0]!.additionalProperties = { type: "result" };
      const item = buildConversationRenderModel([source], schemaOptions)[0]!;
      assert.equal(item.type, "result");
      assert.deepEqual(item.type === "result" ? item.message.contents : [], [
        { type: "json", text: '{\n  "approved": false,\n  "id": 9007199254740993\n}' },
      ]);
      assert.equal(source.contents[0]?.content, json);
    }
  }
});

test("ordinary assistant JSON text keeps its Markdown presentation", () => {
  const json = '{"approved":false}';
  for (const properties of [{}, { resultFormat: "json" }]) {
    const item = buildConversationRenderModel(
      [message("plain", "assistant", json, properties)],
      schemaOptions,
    )[0]!;
    assert.ok(item.type === "result" || item.type === "message");
    assert.deepEqual(item.message.contents, [
      { type: "markdown", markdown: json, sourceType: "TextContent" },
    ]);
  }
});

test("unmarked Result prose, partial JSON and scalar values keep their original presentation", () => {
  for (const text of ['Summary: {"approved":false}', '{"approved":', "true", "null", "42"]) {
    const item = buildConversationRenderModel(
      [message("result", "assistant", text, { type: "result" })],
      schemaOptions,
    )[0]!;
    assert.deepEqual(item.type === "result" ? item.message.contents : [], [
      { type: "markdown", markdown: text, sourceType: "TextContent" },
    ]);
  }
});

test("invalid historical JSON results show an error instead of returning the original prose", () => {
  for (const text of ["### Summary", "true", "null", '"{}"', "{}\n[]", "{invalid}"]) {
    const source = message("invalid-result", "assistant", text);
    source.additionalProperties = { type: "result", resultFormat: "json" };

    const item = buildConversationRenderModel([source], schemaOptions)[0]!;

    assert.equal(item.type, "result");
    assert.deepEqual(
      item.type === "result" ? item.message.contents.map((content) => content.type) : [],
      ["error"],
    );
  }
});

test("unmarked Result JSON stays Markdown without a configured schema", () => {
  const json = '{"approved":false}';
  for (const resultFormat of [undefined, "markdown"] as const) {
    for (const author of ["system-agent", "claude-code", "codex", "pi"]) {
      const source = message("result", "assistant", json, { type: "result" });
      source.author = author;
      const item = buildConversationRenderModel([source], {
        activeAgentId: "agent",
        agentResultFormats: [{ id: "agent", resultFormat }],
      })[0]!;
      assert.deepEqual(item.type === "result" ? item.message.contents : [], [
        { type: "markdown", markdown: json, sourceType: "TextContent" },
      ]);
    }
  }
});

test("the persisted JSON Result marker preserves execution-time format without current Agent configuration", () => {
  const source = message("result", "assistant", '{"approved":false}', {
    type: "result",
    resultFormat: "json",
  });
  for (const options of [
    {},
    {
      ...schemaOptions,
      agentResultFormats: [{ id: "schema-agent", resultFormat: "markdown" as const }],
    },
  ]) {
    const item = buildConversationRenderModel([source], options)[0]!;
    assert.deepEqual(item.type === "result" ? item.message.contents : [], [
      { type: "json", text: '{\n  "approved": false\n}' },
    ]);
  }
});

test("an explicit Markdown Result overrides the Agent's current JSON default", () => {
  const json = '{"approved":false}';
  const source = message("result", "assistant", json, { type: "result", resultFormat: "markdown" });
  const item = buildConversationRenderModel([source], schemaOptions)[0]!;
  assert.deepEqual(item.type === "result" ? item.message.contents : [], [
    { type: "markdown", markdown: json, sourceType: "TextContent" },
  ]);
});

test("history uses each turn's target schema instead of the currently selected Agent", () => {
  const messages: AiMessage[] = [];
  for (const [index, target] of [
    { targetType: "agent", targetId: "schema-agent" },
    { targetType: "agent", targetId: "plain-agent" },
    { targetType: "agentflow", targetId: "schema-agent" },
    { targetType: "agent", targetId: "missing-agent" },
    { targetType: "agent", targetId: "SCHEMA-AGENT" },
  ].entries()) {
    const input = message(`user-${index}`, "user", "Review");
    input.contents[0]!.additionalProperties = target;
    const result = message(`result-${index}`, "assistant", '{"approved":false}', {
      type: "result",
    });
    input.streamingScopeId = result.streamingScopeId = input.messageId;
    messages.push(input, result);
  }
  for (const activeAgentId of ["plain-agent", "schema-agent", null]) {
    const results = buildConversationRenderModel(messages, {
      ...schemaOptions,
      activeAgentId,
    }).filter((item) => item.type === "result");
    assert.deepEqual(
      results.map((item) => item.message.contents[0]?.type),
      ["json", "markdown", "markdown", "markdown", "json"],
    );
  }
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

test("all Todo tools render their matching snapshots at the Tool call position", () => {
  const toolNames = [
    "todos_add",
    "todos_complete",
    "todos_remove",
    "todos_get_remaining",
    "todos_get_all",
  ];
  const toolMessages: AiMessage[] = toolNames.flatMap((toolName, index) => {
    const callId = `todo-call-${index}`;
    return [
      {
        messageId: `call-${index}`,
        role: "assistant",
        streamingScopeId: "user-1",
        contents: [
          {
            type: "FunctionCallContent",
            content: {},
            additionalProperties: { callId, toolName },
          },
        ],
      },
      {
        messageId: `result-${index}`,
        role: "tool",
        streamingScopeId: "user-1",
        contents: [
          {
            type: "FunctionResultContent",
            content: index,
            additionalProperties: { callId },
          },
        ],
      },
    ];
  });
  const snapshots: AiMessage[] = toolNames.map((toolName, index) => ({
    messageId: `snapshot-${index}`,
    role: "system",
    streamingScopeId: "user-1",
    contents: [{ type: "TextContent", content: "" }],
    additionalProperties: {
      type: "tool-todo-snapshot",
      callId: `todo-call-${index}`,
      toolName,
      items: [{ id: index + 1, title: toolName, isComplete: false }],
    },
  }));

  const items = buildConversationRenderModel([
    ...toolMessages,
    message("final", "assistant", "Finished"),
    ...snapshots,
  ]);

  assert.deepEqual(
    items.map((item) => item.type),
    ["tool-state", "tool-state", "tool-state", "tool-state", "tool-state", "message"],
  );
  assert.deepEqual(
    items
      .filter((item) => item.type === "tool-state")
      .map((item) => item.message.additionalProperties?.toolName),
    toolNames,
  );
});

test("Todo cards exclude items created in earlier turns", () => {
  const todoToolTurn = (
    scopeId: string,
    callId: string,
    items: Array<{ id: number; title: string; isComplete: boolean }>,
  ): AiMessage[] => [
    {
      messageId: `${callId}-call`,
      role: "assistant",
      streamingScopeId: scopeId,
      contents: [
        {
          type: "FunctionCallContent",
          content: {},
          additionalProperties: { callId, toolName: "todos_add" },
        },
      ],
    },
    {
      messageId: `${callId}-result`,
      role: "tool",
      streamingScopeId: scopeId,
      contents: [
        {
          type: "FunctionResultContent",
          content: items.at(-1),
          additionalProperties: { callId },
        },
      ],
    },
    {
      messageId: `${callId}-snapshot`,
      role: "system",
      streamingScopeId: scopeId,
      contents: [{ type: "TextContent", content: "" }],
      additionalProperties: {
        type: "tool-todo-snapshot",
        callId,
        toolName: "todos_add",
        items,
      },
    },
  ];
  const firstTodo = { id: 1, title: "First turn", isComplete: true };
  const secondTodo = { id: 2, title: "Second turn", isComplete: false };

  const messages = [
    ...todoToolTurn("turn-1", "call-1", [firstTodo]),
    ...todoToolTurn("turn-2", "call-2", [firstTodo, secondTodo]),
  ];
  const rendered = buildConversationRenderModel(messages);
  const cards = rendered.filter((item) => item.type === "tool-state");

  assert.equal(cards.length, 2);
  assert.deepEqual(cards[0]?.message.additionalProperties?.items, [firstTodo]);
  assert.deepEqual(cards[1]?.message.additionalProperties?.items, [secondTodo]);
  assert.deepEqual(getCurrentTurnTodoItems(messages), [secondTodo]);
  assert.deepEqual(
    getCurrentTurnTodoItems([
      ...messages,
      {
        messageId: "turn-3-user",
        role: "user",
        streamingScopeId: "turn-3",
        contents: [{ type: "TextContent", content: "New turn" }],
      },
    ]),
    [],
  );
});
