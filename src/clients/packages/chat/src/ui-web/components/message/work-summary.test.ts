import assert from "node:assert/strict";
import test from "node:test";
import { installLayoutMetrics, setupDomEnvironment } from "@agw/test-harness";

import type { AiMessage } from "@agw/api";

const environment = await setupDomEnvironment();
const { React, act, fireEvent, render, screen } = environment;
installLayoutMetrics(environment.window);
const { Conversation } = await import("./conversation");
const { buildConversationRenderModel } = await import("@agw/chat-core");

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
    ...Array.from({ length: 80 }, (_, index): AiMessage => ({
      messageId: `later-${index}`,
      role: "user",
      contents: [{ type: "TextContent", content: `Later ${index}` }],
    })),
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
