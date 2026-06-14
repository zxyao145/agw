import assert from "node:assert/strict";
import test from "node:test";

import { handleSystemMessage } from "./ai-message-handlers.ts";

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
