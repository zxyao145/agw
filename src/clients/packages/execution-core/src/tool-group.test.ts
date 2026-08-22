import assert from "node:assert/strict";
import test from "node:test";

import type { ExecutionMessage } from "./types";
import { createMessageFragments, processMessages } from "./tool-group";

function toolMessage(type: string, scope: string, callId = "item_1"): ExecutionMessage {
  return toolContentsMessage(type, scope, [callId]);
}

function toolContentsMessage(type: string, scope: string, callIds: string[]): ExecutionMessage {
  return {
    messageId: `${type}-${scope}`,
    author: "agent",
    role: type === "FunctionCallContent" ? "assistant" : "tool",
    streamingScopeId: scope,
    contents: callIds.map((callId) => ({
      type,
      content: `${type}-${callId}`,
      additionalProperties: {
        callId,
        ...(type === "FunctionCallContent" ? { toolName: `tool-${callId}` } : {}),
      },
    })),
  };
}

test("processMessages reuses preprocessing for unchanged messages only", () => {
  const stableMessage: ExecutionMessage = {
    messageId: "stable-message",
    author: "agent",
    role: "assistant",
    streamingScopeId: "user-1",
    contents: [{ type: "TextContent", content: "stable" }],
  };
  const activeMessage: ExecutionMessage = {
    messageId: "active-message",
    author: "agent",
    role: "assistant",
    streamingScopeId: "user-1",
    contents: [{ type: "TextContent", content: "first" }],
  };
  const nextActiveMessage = {
    ...activeMessage,
    contents: [{ type: "TextContent", content: "second" }],
  };

  const first = processMessages([stableMessage, activeMessage]);
  const second = processMessages([stableMessage, nextActiveMessage]);

  assert.equal(first[0].type, "normal");
  assert.equal(second[0].type, "normal");
  assert.equal(first[0].message, second[0].message);
  assert.notEqual(first[1].message, second[1].message);
});

test("processMessages restores persisted authorless assistant and tool messages", () => {
  const items = processMessages([
    {
      messageId: "assistant-1",
      author: null,
      role: "assistant",
      streamingScopeId: "user-1",
      contents: [
        { type: "TextReasoningContent", content: "Planning the task" },
        {
          type: "FunctionCallContent",
          content: "{}",
          additionalProperties: { callId: "call-1", toolName: "todos_add" },
        },
      ],
    },
    {
      messageId: "",
      author: null,
      role: "tool",
      streamingScopeId: "user-1",
      contents: [
        {
          type: "FunctionResultContent",
          content: "[]",
          additionalProperties: { callId: "call-1" },
        },
      ],
    },
  ]);

  assert.deepEqual(
    items.map((item) => item.type),
    ["normal", "accordion"],
  );
});

test("duplicate call ids produce one tool group per turn", () => {
  const items = processMessages([
    toolMessage("FunctionCallContent", "user-1"),
    toolMessage("FunctionResultContent", "user-1"),
    toolMessage("FunctionCallContent", "user-2"),
    toolMessage("FunctionResultContent", "user-2"),
  ]);

  assert.equal(items.length, 2);
  assert.deepEqual(
    items.map((item) => item.type),
    ["accordion", "accordion"],
  );
  assert.deepEqual(
    items.map((item) =>
      item.type === "accordion" ? item.messages.map((message) => message.streamingScopeId) : [],
    ),
    [
      ["user-1", "user-1"],
      ["user-2", "user-2"],
    ],
  );
});

test("concurrent tool calls pair with out-of-order results in call order", () => {
  const items = processMessages([
    toolContentsMessage("FunctionCallContent", "user-1", ["call-1", "call-2", "call-3"]),
    toolContentsMessage("FunctionResultContent", "user-1", ["call-3", "call-1", "call-2"]),
  ]);

  assert.deepEqual(
    items.map((item) => item.type),
    ["accordion", "accordion", "accordion"],
  );
  assert.deepEqual(
    items.map((item) => (item.type === "accordion" ? item.toolName : "")),
    ["tool-call-1", "tool-call-2", "tool-call-3"],
  );
  assert.deepEqual(
    items.map((item) =>
      item.type === "accordion"
        ? item.messages.map((message) => message.contents[0].additionalProperties?.callId)
        : [],
    ),
    [
      ["call-1", "call-1"],
      ["call-2", "call-2"],
      ["call-3", "call-3"],
    ],
  );
});

test("final result messages keep their result classification", () => {
  const finalResult: ExecutionMessage = {
    messageId: "final-result",
    author: "agent",
    role: "assistant",
    contents: [{ type: "TextContent", content: "done" }],
    additionalProperties: { type: "result" },
  };

  const items = processMessages([finalResult]);

  assert.deepEqual(items, [{ type: "result", message: finalResult }]);
});

test("mixed ordinary and unmatched tool contents preserve content order", () => {
  const message: ExecutionMessage = {
    messageId: "mixed-message",
    author: "agent",
    role: "assistant",
    streamingScopeId: "user-1",
    contents: [
      { type: "TextContent", content: "before" },
      {
        type: "FunctionCallContent",
        content: "call",
        additionalProperties: { callId: "call-without-result", toolName: "orphan-call" },
      },
      { type: "TextContent", content: "after" },
      {
        type: "FunctionResultContent",
        content: "result",
        additionalProperties: { callId: "result-without-call" },
      },
    ],
  };

  const items = processMessages([message]);

  assert.deepEqual(
    items.map((item) => item.type),
    ["normal", "normal", "normal", "normal"],
  );
  assert.deepEqual(
    items.map((item) =>
      item.type === "normal" ? item.message.contents.map((content) => content.type) : [],
    ),
    [["TextContent"], ["FunctionCallContent"], ["TextContent"], ["FunctionResultContent"]],
  );
});

test("createMessageFragments pairs a call and result only within the same scope", () => {
  const call = toolMessage("FunctionCallContent", "user-1", "call-1");
  const result = toolMessage("FunctionResultContent", "user-2", "call-1");

  const items = processMessages([call, result]);

  assert.equal(items.length, 2);
  assert.deepEqual(
    items.map((item) => item.type),
    ["normal", "normal"],
  );
});

test("createMessageFragments returns stable fragment references for the same message", () => {
  const message = toolMessage("FunctionCallContent", "user-1", "call-1");

  const first = createMessageFragments(message);
  const second = createMessageFragments(message);

  assert.equal(first, second);
});

test("processMessages renders authorless user messages unless presentation filtering hides them", () => {
  const userMessage: ExecutionMessage = {
    messageId: "user-1",
    role: "user",
    contents: [{ type: "TextContent", content: "visible" }],
  };
  const items = processMessages([userMessage]);

  assert.deepEqual(items, [{ type: "normal", message: userMessage }]);
});

test("processMessages renders authorless system messages instead of skipping all system messages", () => {
  const systemMessage: ExecutionMessage = {
    messageId: "system-1",
    role: "system",
    author: "agent",
    contents: [{ type: "TextContent", content: "visible system" }],
  };
  const items = processMessages([systemMessage]);

  assert.deepEqual(
    items.map((item) => item.type),
    ["normal"],
  );
});
