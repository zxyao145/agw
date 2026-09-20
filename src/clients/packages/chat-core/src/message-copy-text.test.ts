import assert from "node:assert/strict";
import test from "node:test";
import { getMessageCopyText } from "./message-copy-text";
import { createUserTextMessage, toExecutionUserInput } from "./message-factory";

test("copy preserves Markdown and joins presented text while skipping image attachments", () => {
  const markdown = "# Heading\n\n**bold** [link](https://example.com)\n\n```ts\n  code();\n```";
  assert.equal(
    getMessageCopyText([
      { type: "markdown", sourceType: "TextContent", markdown },
      { type: "image", uri: "data:image/png;base64,a", name: "image" },
      { type: "json", text: '{"value":9007199254740993}' },
      {
        type: "plan",
        leadingMarkdown: "Before",
        markdown: "- Step",
        trailingMarkdown: "After",
        isClosed: true,
      },
      { type: "reasoning", markdown: "Full reasoning", preview: "Full" },
    ]),
    `${markdown}\n\n{"value":9007199254740993}\n\nBefore\n\n- Step\n\nAfter\n\nFull reasoning`,
  );
  assert.equal(
    getMessageCopyText([{ type: "uri", uri: "https://example.com/image.png", name: null }]),
    "",
  );
});

test("user message creation carries the same timestamp into execution input", () => {
  const before = Date.now();
  const message = createUserTextMessage("question");
  assert.ok(message.createdAt);
  assert.ok(Date.parse(message.createdAt) >= before);
  assert.ok(Date.parse(message.createdAt) <= Date.now());
  assert.equal(toExecutionUserInput(message).createdAt, message.createdAt);
});
