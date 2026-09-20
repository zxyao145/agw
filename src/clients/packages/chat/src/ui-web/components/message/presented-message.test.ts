import assert from "node:assert/strict";
import test from "node:test";
import React from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { buildConversationRenderModel } from "@agw/chat-core";
import { PresentedMessageComponent } from "./presented-message";

test("structured Result renders JSON as literal text without a Markdown code block", () => {
  const text = '[{"text":"**literal** <script>alert(1)</script>","id":9007199254740993}]';
  const item = buildConversationRenderModel(
    [
      {
        messageId: "result",
        role: "assistant",
        author: "$agw-server",
        additionalProperties: { type: "result", resultFormat: "json" },
        contents: [{ type: "TextContent", content: `\`\`\`json\n${text}\n\`\`\`\n小结：完成。` }],
      },
    ],
    {
      activeAgentId: "agent",
      agentResultFormats: [{ id: "agent", resultFormat: "json" }],
    },
  )[0]!;
  assert.equal(item.type, "result");
  if (item.type !== "result") return;

  const html = renderToStaticMarkup(
    React.createElement(PresentedMessageComponent, { message: item.message }),
  );

  assert.match(html, /<pre[^>]*>/);
  assert.match(html, /\[\n  \{\n    &quot;text&quot;: /);
  assert.match(html, /\n    &quot;id&quot;: 9007199254740993\n  \}\n\]/);
  assert.match(html, /9007199254740993/);
  assert.match(html, /\*\*literal\*\*/);
  assert.match(html, /&lt;script&gt;/);
  assert.doesNotMatch(html, /<script|<strong|msg-content-md|```|小结/);
});

test("Claude Code native Result renders indented JSON without an Agw resultFormat marker", () => {
  const item = buildConversationRenderModel(
    [
      {
        messageId: "claude-result",
        role: "assistant",
        author: "claude-code",
        additionalProperties: { type: "result", subtype: "success", totalCostUsd: 0.01 },
        contents: [
          { type: "TextContent", content: '{"approved":false,"issues":[{"line":3}]}' },
          { type: "UsageContent", content: { totalTokenCount: 10 } },
        ],
      },
    ],
    {
      activeAgentId: "agent",
      agentResultFormats: [{ id: "agent", resultFormat: "json" }],
    },
  )[0]!;
  assert.equal(item.type, "result");
  if (item.type !== "result") return;

  const html = renderToStaticMarkup(
    React.createElement(PresentedMessageComponent, { message: item.message }),
  );

  assert.match(html, /<pre[^>]*>\{\n  &quot;approved&quot;: false,/);
  assert.match(html, /\n  &quot;issues&quot;: \[\n    \{\n      &quot;line&quot;: 3/);
  assert.doesNotMatch(html, /msg-content-md/);
});
