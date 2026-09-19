import assert from "node:assert/strict";
import test from "node:test";
import React from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { buildConversationRenderModel } from "@agw/chat-core";
import { PresentedMessageComponent } from "./presented-message";

test("structured Result renders JSON as literal text without a Markdown code block", () => {
  const text = '[{"text":"**literal** <script>alert(1)</script>","id":9007199254740993}]';
  const item = buildConversationRenderModel([
    {
      messageId: "result",
      role: "assistant",
      author: "$agw-server",
      additionalProperties: { type: "result", resultFormat: "json" },
      contents: [{ type: "TextContent", content: `\`\`\`json\n${text}\n\`\`\`\n小结：完成。` }],
    },
  ])[0]!;
  assert.equal(item.type, "result");
  if (item.type !== "result") return;

  const html = renderToStaticMarkup(
    React.createElement(PresentedMessageComponent, { message: item.message }),
  );

  assert.match(html, /<pre[^>]*>/);
  assert.match(html, /9007199254740993/);
  assert.match(html, /\*\*literal\*\*/);
  assert.match(html, /&lt;script&gt;/);
  assert.doesNotMatch(html, /<script|<strong|msg-content-md|```|小结/);
});
