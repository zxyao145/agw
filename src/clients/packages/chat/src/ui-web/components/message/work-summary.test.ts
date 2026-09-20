import assert from "node:assert/strict";
import { register } from "node:module";
import test, { after, afterEach } from "node:test";
import { JSDOM } from "jsdom";
import * as React from "react";
import type { AiMessage } from "@agw/api";

// Resolve the runtime package's React peer to this renderer's React, as app bundlers do.
register(
  "data:text/javascript," +
    encodeURIComponent(`
    let reactUrl;
    export function initialize(data) { reactUrl = data.reactUrl; }
    export function resolve(specifier, context, nextResolve) {
      return specifier === "react" ? { url: reactUrl, shortCircuit: true } : nextResolve(specifier, context);
    }
  `),
  { data: { reactUrl: import.meta.resolve("react") } },
);

const dom = new JSDOM("<!doctype html><html><body></body></html>", {
  pretendToBeVisual: true,
  url: "http://localhost/",
});
const { window } = dom;
for (const [name, value] of Object.entries({
  window,
  document: window.document,
  navigator: window.navigator,
  HTMLElement: window.HTMLElement,
  Element: window.Element,
  Node: window.Node,
  getComputedStyle: window.getComputedStyle.bind(window),
  requestAnimationFrame: window.requestAnimationFrame.bind(window),
  cancelAnimationFrame: window.cancelAnimationFrame.bind(window),
}))
  Object.defineProperty(globalThis, name, { configurable: true, value });
Object.defineProperty(window.HTMLElement.prototype, "offsetHeight", {
  configurable: true,
  get() {
    return this.dataset.index === undefined ? 600 : 72;
  },
});
Object.defineProperty(window.HTMLElement.prototype, "offsetWidth", {
  configurable: true,
  get: () => 800,
});
window.HTMLElement.prototype.scrollTo = function (options) {
  if (typeof options === "object") this.scrollTop = options.top ?? this.scrollTop;
};
(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT =
  true;
const { act, cleanup, fireEvent, render, screen } = await import("@testing-library/react");
const { Conversation } = await import("./conversation");
const { buildConversationRenderModel } = await import("@agw/chat-core");
afterEach(cleanup);
after(() => window.close());

const messages: AiMessage[] = [
  {
    messageId: "user",
    role: "user",
    createdAt: "2026-09-20T01:00:00Z",
    contents: [{ type: "TextContent", content: "Review the change" }],
  },
  {
    messageId: "process",
    role: "assistant",
    contents: [{ type: "TextContent", content: "Checking implementation" }],
  },
  {
    messageId: "result",
    role: "assistant",
    createdAt: "2026-09-20T01:17:39Z",
    additionalProperties: { type: "result" },
    contents: [{ type: "TextContent", content: "Review complete" }],
  },
];
function Harness({
  active = false,
  conversationKey = "a",
  history = messages,
  onToggle = () => {},
}: {
  active?: boolean;
  conversationKey?: string;
  history?: AiMessage[];
  onToggle?: () => void;
}) {
  const ref = React.useRef<HTMLDivElement>(null);
  const [ready, setReady] = React.useState(false);
  React.useLayoutEffect(() => setReady(true), []);
  const items = React.useMemo(
    () => buildConversationRenderModel(history, { isCurrentTurnActive: active }),
    [history, active],
  );
  return React.createElement(
    "div",
    { ref, "data-testid": "scroller" },
    ready &&
      React.createElement(Conversation, {
        items,
        conversationKey,
        scrollElementRef: ref,
        onWorkSummaryToggle: onToggle,
      }),
  );
}

test("completed work defaults closed, preserves Result actions and toggles process rows", async () => {
  let toggles = 0;
  const view = render(React.createElement(Harness, { onToggle: () => toggles++ }));
  const trigger = await screen.findByRole("button", { name: "Worked for 17m 39s" });
  assert.equal(trigger.getAttribute("aria-expanded"), "false");
  assert.ok(screen.getByText("Review the change"));
  assert.ok(screen.getByText("Review complete"));
  assert.equal(screen.queryByText("Checking implementation"), null);
  assert.equal(screen.getAllByRole("button", { name: "Copy message" }).length, 2);
  assert.equal(
    view.container.querySelector('[data-msg-id="result"]')?.className.includes("border-t"),
    false,
  );
  fireEvent.click(trigger);
  assert.equal(trigger.getAttribute("aria-expanded"), "true");
  assert.ok(screen.getByText("Checking implementation"));
  assert.equal(toggles, 1);
  fireEvent.click(trigger);
  assert.equal(screen.queryByText("Checking implementation"), null);
});

test("expansion survives rerenders and virtual unmounts, resets for resumed turns and new conversations", async () => {
  const view = render(React.createElement(Harness));
  fireEvent.click(await screen.findByRole("button", { name: "Worked for 17m 39s" }));
  const manyMessages = [
    ...messages,
    ...Array.from(
      { length: 80 },
      (_, index): AiMessage => ({
        messageId: `later-${index}`,
        role: "user",
        contents: [{ type: "TextContent", content: `Later ${index}` }],
      }),
    ),
  ];
  view.rerender(React.createElement(Harness, { history: manyMessages }));
  assert.ok(screen.getByText("Checking implementation"));
  const scroller = screen.getByTestId("scroller");
  await act(async () => {
    scroller.scrollTop = 4500;
    fireEvent.scroll(scroller);
  });
  assert.equal(screen.queryByRole("button", { name: "Worked for 17m 39s" }), null);
  await act(async () => {
    scroller.scrollTop = 0;
    fireEvent.scroll(scroller);
  });
  assert.equal(
    screen.getByRole("button", { name: "Worked for 17m 39s" }).getAttribute("aria-expanded"),
    "true",
  );
  view.rerender(React.createElement(Harness, { active: true }));
  assert.equal(screen.queryByRole("button", { name: "Worked for 17m 39s" }), null);
  assert.ok(screen.getByText("Checking implementation"));
  view.rerender(React.createElement(Harness));
  assert.equal(
    screen.getByRole("button", { name: "Worked for 17m 39s" }).getAttribute("aria-expanded"),
    "false",
  );
  fireEvent.click(screen.getByRole("button", { name: "Worked for 17m 39s" }));
  view.rerender(React.createElement(Harness, { conversationKey: "b" }));
  assert.equal(screen.queryByText("Checking implementation"), null);
});
