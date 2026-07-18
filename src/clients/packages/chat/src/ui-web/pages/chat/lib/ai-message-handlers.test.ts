import assert from "node:assert/strict";
import test from "node:test";

import {
  collapseConsecutiveSystemMessages,
  getClaudeInitCommands,
  handleSystemMessage,
  prepareClaudeHistory,
} from "../../../../lib/chat/ai-message-handlers.ts";

test("consecutive system messages keep only the latest message in each sequence", () => {
  const assistant = {
    messageId: "assistant-1",
    role: "assistant",
    contents: [{ type: "TextContent", content: "Before" }],
  };
  const firstRetry = {
    messageId: "system-1",
    role: "system",
    contents: [{ type: "ErrorContent", content: "Retry 1" }],
  };
  const secondRetry = {
    messageId: "system-2",
    role: "system",
    contents: [{ type: "ErrorContent", content: "Retry 2" }],
  };
  const user = {
    messageId: "user-1",
    role: "user",
    contents: [{ type: "TextContent", content: "Continue" }],
  };
  const thirdRetry = {
    messageId: "system-3",
    role: "system",
    contents: [{ type: "ErrorContent", content: "Retry 3" }],
  };
  const result = {
    messageId: "result-1",
    role: "system",
    contents: [{ type: "TextContent", content: "Done" }],
    additionalProperties: { type: "result" },
  };

  assert.deepEqual(
    collapseConsecutiveSystemMessages([
      assistant,
      firstRetry,
      secondRetry,
      user,
      thirdRetry,
      result,
    ]),
    [assistant, secondRetry, user, result],
  );
});

test("system result with top-level marker stops executing and appends the message", () => {
  const message = {
    messageId: "result-1",
    author: "$agw-server",
    role: "system",
    contents: [{ type: "TextContent", content: "Done" }],
    additionalProperties: { type: "result" },
  };

  assert.deepEqual(handleSystemMessage(message), [
    { type: "setIsExecuting", value: false },
    { type: "append", message },
  ]);
});

test("system result with content-level marker stops executing and appends the message", () => {
  const message = {
    messageId: "result-2",
    author: "$agw-server",
    role: "system",
    contents: [
      {
        type: "TextContent",
        content: "Done",
        additionalProperties: { type: "result" },
      },
    ],
  };

  assert.deepEqual(handleSystemMessage(message), [
    { type: "setIsExecuting", value: false },
    { type: "append", message },
  ]);
});

test("interrupted hint stops executing without appending the message", () => {
  const message = {
    messageId: "hint-1",
    author: "$agw-server",
    role: "system",
    contents: [{ type: "TextContent", content: "Interrupted by user" }],
    additionalProperties: { subtype: "hint" },
  };

  assert.deepEqual(handleSystemMessage(message), [{ type: "setIsExecuting", value: false }]);
});

test("Claude init commands are extracted without appending the init message", () => {
  const message = {
    messageId: "init-1",
    author: "ClaudeCode",
    role: "system",
    contents: [
      {
        type: "TextContent",
        content: JSON.stringify({ slash_commands: ["compact", "/review", 42] }),
      },
    ],
    additionalProperties: { subtype: "init" },
  };

  assert.deepEqual(getClaudeInitCommands(message), ["compact", "/review"]);
  assert.deepEqual(handleSystemMessage(message), [
    { type: "setClaudeCommands", commands: ["compact", "/review"] },
  ]);
});

test("malformed or incomplete Claude init metadata falls back to no commands", () => {
  const malformed = {
    messageId: "init-malformed",
    role: "system",
    contents: [{ type: "TextContent", content: "{bad json" }],
    additionalProperties: { subtype: "init" },
  };
  const incomplete = {
    messageId: "init-incomplete",
    role: "system",
    contents: [{ type: "TextContent", content: JSON.stringify({ tools: [] }) }],
    additionalProperties: { subtype: "init" },
  };

  assert.deepEqual(getClaudeInitCommands(malformed), []);
  assert.deepEqual(getClaudeInitCommands(incomplete), []);
});

test("history removes init and control messages and restores the latest valid commands", () => {
  const visibleMessage = {
    messageId: "visible",
    role: "assistant",
    contents: [{ type: "TextContent", content: "Hello" }],
  };
  const history = [
    {
      messageId: "init-old",
      role: "system",
      contents: [{ type: "TextContent", content: JSON.stringify({ slash_commands: ["old"] }) }],
      additionalProperties: { subtype: "init" },
    },
    visibleMessage,
    {
      messageId: "init-new",
      role: "system",
      contents: [{ type: "TextContent", content: JSON.stringify({ slash_commands: ["new"] }) }],
      additionalProperties: { subtype: "init" },
    },
    {
      messageId: "init-invalid",
      role: "system",
      contents: [{ type: "TextContent", content: "invalid" }],
      additionalProperties: { subtype: "init" },
    },
    {
      messageId: "turn-start",
      role: "assistant",
      author: "Agw",
      contents: [{ type: "TextContent", content: "started" }],
      additionalProperties: { type: "turn-start" },
    },
    {
      messageId: "turn-finished",
      role: "assistant",
      author: "Agw",
      contents: [{ type: "TextContent", content: "finished" }],
      additionalProperties: { type: "turn-finished", status: "completed" },
    },
  ];

  assert.deepEqual(prepareClaudeHistory(history), {
    messages: [visibleMessage],
    commands: ["new"],
  });
});
