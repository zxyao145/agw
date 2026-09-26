import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";

import type { AiMessage } from "@agw/api";
import {
  buildConversationRenderModel,
  type PresentedContent,
  type PresentedMessage,
} from "@agw/chat-core";

const { React, fireEvent, observers, render, screen } = await setupDomEnvironment();
const { PresentedMessageComponent } = await import("./presented-message.tsx");

const IMAGE_DATA_URL =
  "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

function presented(
  contents: PresentedContent[],
  source: Partial<AiMessage> = {},
  overrides: Partial<PresentedMessage> = {},
): PresentedMessage {
  return {
    source: { messageId: "message-1", role: "assistant", contents: [], ...source },
    identity: "message-1",
    alignment: "left",
    width: "normal",
    meta: null,
    contents,
    ...overrides,
  };
}

function renderMessage(message: PresentedMessage, streaming = false) {
  return render(React.createElement(PresentedMessageComponent, { message, streaming }));
}

// jsdom 不做排版：给滚动区域设定 320px 可见高度和内容高度，按浏览器规则限制 scrollTop，并投递尺寸变化通知。
// jsdom performs no layout: give the scroll area a 320px visible height and a content height, clamp
// scrollTop the way a browser does, and deliver resize notifications.
function scrollArea(container: HTMLElement) {
  const element = container.querySelector<HTMLElement>(".max-h-80");
  assert.ok(element);
  const clientHeight = 320;
  let contentHeight = 0;
  let scrollTop = 0;
  const scrollHeight = () => Math.max(contentHeight, clientHeight);
  Object.defineProperties(element, {
    clientHeight: { configurable: true, value: clientHeight },
    scrollHeight: { configurable: true, get: scrollHeight },
    scrollTop: {
      configurable: true,
      get: () => scrollTop,
      set: (value: number) => {
        scrollTop = Math.min(Math.max(value, 0), scrollHeight() - clientHeight);
      },
    },
  });

  return {
    element,
    grow(height: number) {
      contentHeight = height;
      for (const entry of observers.resize.filter(
        (observed) => observed.target === element.firstElementChild,
      )) {
        entry.callback([], null);
      }
    },
    scroll(top: number) {
      element.scrollTop = top;
      fireEvent.scroll(element);
    },
  };
}

test("markdown content renders through the shared Markdown renderer", () => {
  renderMessage(
    presented([{ type: "markdown", markdown: "# Review complete", sourceType: "TextContent" }]),
  );

  assert.ok(screen.getByRole("heading", { name: "Review complete", level: 1 }));
});

test("plain content renders as literal text", () => {
  renderMessage(presented([{ type: "plain", text: "# not a heading", sourceType: "TextContent" }]));

  assert.equal(screen.queryByRole("heading"), null);
  assert.ok(screen.getByText("# not a heading"));
});

test("structured result content keeps its JSON formatting", () => {
  const view = renderMessage(presented([{ type: "json", text: '{\n  "approved": false\n}' }]));

  assert.equal(view.container.querySelector("pre")?.textContent, '{\n  "approved": false\n}');
});

test("error content renders the failure text", () => {
  renderMessage(presented([{ type: "error", text: "Execution failed: timeout" }]));

  assert.ok(screen.getByText("Execution failed: timeout"));
});

test("reasoning content starts collapsed and expands on request", () => {
  const reasoning = "step ".repeat(60).trim();
  renderMessage(presented([{ type: "reasoning", markdown: reasoning, preview: "step step" }]));

  const toggle = screen.getByRole("button", { name: "Expand reasoning" });
  assert.equal(toggle.getAttribute("aria-expanded"), "false");
  const collapsedLength = screen.getByText(/step/).textContent?.length ?? 0;
  assert.ok(collapsedLength < reasoning.length);

  fireEvent.click(toggle);

  assert.ok(screen.getByRole("button", { name: "Collapse reasoning" }));
  assert.equal(screen.getByText(/step/).textContent, reasoning);
});

test("image content renders the attachment with its name as alternative text", () => {
  const view = renderMessage(
    presented([{ type: "image", uri: IMAGE_DATA_URL, name: "screen.png" }]),
  );
  const image = view.container.querySelector("img");

  assert.equal(image?.getAttribute("src"), IMAGE_DATA_URL);
  assert.equal(image?.getAttribute("alt"), "screen.png");
});

test("an unsupported image payload renders nothing", () => {
  const view = renderMessage(
    presented([{ type: "image", uri: "data:text/plain;base64,QUJD", name: null }]),
  );

  assert.equal(view.container.querySelector("img"), null);
});

test("plan content renders the Plan Card", () => {
  renderMessage(
    presented([
      {
        type: "plan",
        leadingMarkdown: "",
        markdown: "# Rollout",
        trailingMarkdown: "",
        isClosed: true,
      },
    ]),
  );

  assert.ok(screen.getByRole("heading", { name: "Plan", level: 2 }));
  assert.ok(screen.getByRole("heading", { name: "Rollout", level: 1 }));
});

test("a result message is titled and offers message actions", () => {
  renderMessage(
    presented(
      [{ type: "markdown", markdown: "Done", sourceType: "TextContent" }],
      { additionalProperties: { type: "result" } },
      { width: "full" },
    ),
  );

  assert.ok(screen.getByText("Result"));
  assert.ok(screen.getByRole("button", { name: "Copy message" }));
});

test("an ordinary agent message carries no title and no message actions", () => {
  renderMessage(presented([{ type: "markdown", markdown: "Working", sourceType: "TextContent" }]));

  assert.equal(screen.queryByText("Result"), null);
  assert.equal(screen.queryByRole("button", { name: "Copy message" }), null);
});

test("tool use and tool result messages are titled by their direction", () => {
  const view = renderMessage(
    presented([{ type: "json", text: "{}" }], {
      contents: [{ type: "FunctionCallContent", content: "{}" }],
    }),
  );
  assert.ok(screen.getByText("Tool use"));
  view.unmount();

  renderMessage(
    presented([{ type: "json", text: "{}" }], {
      role: "tool",
      contents: [{ type: "FunctionResultContent", content: "{}" }],
    }),
  );
  assert.ok(screen.getByText("Tool result"));
});

test("several contents render in order inside one message", () => {
  const view = renderMessage(
    presented([
      { type: "plain", text: "first", sourceType: "TextContent" },
      { type: "plain", text: "second", sourceType: "TextContent" },
    ]),
  );
  const html = view.container.innerHTML;

  assert.ok(html.indexOf("first") < html.indexOf("second"));
});

test("a user message scrolls its content under the height cap and keeps message actions outside it", () => {
  const view = renderMessage(
    presented(
      [{ type: "plain", text: "long question", sourceType: "TextContent" }],
      { role: "user" },
      { alignment: "right" },
    ),
  );
  const area = view.container.querySelector(".max-h-80");

  assert.ok(area);
  assert.ok(area.contains(screen.getByText("long question")));
  assert.equal(area.contains(screen.getByRole("button", { name: "Copy message" })), false);
});

test("a result message renders without the height cap", () => {
  const view = renderMessage(
    presented(
      [{ type: "markdown", markdown: "Done", sourceType: "TextContent" }],
      { additionalProperties: { type: "result" } },
      { width: "full" },
    ),
  );

  assert.equal(view.container.querySelector(".max-h-80"), null);
});

test("a streaming message stays at the bottom as it grows until the user scrolls up, and resumes at the bottom", () => {
  const view = renderMessage(
    presented([{ type: "markdown", markdown: "Streaming", sourceType: "TextContent" }]),
    true,
  );
  const area = scrollArea(view.container);

  area.grow(800);
  assert.equal(area.element.scrollTop, 480);

  area.scroll(100);
  area.grow(1200);
  assert.equal(area.element.scrollTop, 100);

  area.scroll(880);
  area.grow(1500);
  assert.equal(area.element.scrollTop, 1180);
});

test("a message outside the streaming turn keeps its scroll position as it grows", () => {
  const view = renderMessage(
    presented([{ type: "markdown", markdown: "History", sourceType: "TextContent" }]),
  );
  const area = scrollArea(view.container);

  area.grow(800);

  assert.equal(area.element.scrollTop, 0);
});

test("a user message in the streaming turn keeps its scroll position as it grows", () => {
  const view = renderMessage(
    presented(
      [{ type: "plain", text: "question", sourceType: "TextContent" }],
      { role: "user" },
      { alignment: "right" },
    ),
    true,
  );
  const area = scrollArea(view.container);

  area.grow(800);

  assert.equal(area.element.scrollTop, 0);
});

function renderResult(message: AiMessage) {
  const item = buildConversationRenderModel([message], {
    activeAgentId: "agent",
    agentResultFormats: [{ id: "agent", resultFormat: "json" }],
  })[0]!;
  assert.equal(item.type, "result");
  if (item.type !== "result") return null;
  return render(React.createElement(PresentedMessageComponent, { message: item.message }));
}

test("structured Result renders JSON as literal text without a Markdown code block", () => {
  const text = '[{"text":"**literal** <script>alert(1)</script>","id":9007199254740993}]';
  const view = renderResult({
    messageId: "result",
    role: "assistant",
    author: "$agw-server",
    additionalProperties: { type: "result", resultFormat: "json" },
    contents: [{ type: "TextContent", content: `\`\`\`json\n${text}\n\`\`\`\n小结：完成。` }],
  })!;
  const pre = view.container.querySelector("pre");

  assert.ok(pre);
  assert.match(pre.textContent ?? "", /"text": "\*\*literal\*\* <script>alert\(1\)<\/script>"/);
  assert.match(pre.textContent ?? "", /"id": 9007199254740993/);
  assert.match(pre.textContent ?? "", /\*\*literal\*\*/);
  assert.equal(view.container.querySelector("script"), null);
  assert.equal(view.container.querySelector("strong"), null);
  assert.equal(view.container.querySelector(".msg-content-md"), null);
  assert.equal(view.container.textContent?.includes("小结"), false);
});

test("Claude Code native Result renders indented JSON without an Agw resultFormat marker", () => {
  const view = renderResult({
    messageId: "claude-result",
    role: "assistant",
    author: "claude-code",
    additionalProperties: { type: "result", subtype: "success", totalCostUsd: 0.01 },
    contents: [
      { type: "TextContent", content: '{"approved":false,"issues":[{"line":3}]}' },
      { type: "UsageContent", content: { totalTokenCount: 10 } },
    ],
  })!;
  const pre = view.container.querySelector("pre");

  assert.ok(pre);
  assert.match(pre.textContent ?? "", /\{\n  "approved": false,/);
  assert.match(pre.textContent ?? "", /\n  "issues": \[\n    \{\n      "line": 3/);
  assert.equal(view.container.querySelector(".msg-content-md"), null);
});
