import assert from "node:assert/strict";
import test from "node:test";
import { installLayoutMetrics, setupDomEnvironment } from "@agw/test-harness";

import type { AiMessage, ConversationHistoryTurn } from "@agw/api";

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
  historyTurns = [],
  onToggle = () => {},
  onExpansionChange = () => {},
  hasOlderMessages = false,
  autoLoadBlockedSummaryKey = null,
  onAutoLoadOlderMessages,
}: {
  active?: boolean;
  conversationKey?: string;
  history?: AiMessage[];
  historyTurns?: ConversationHistoryTurn[];
  onToggle?: () => void;
  onExpansionChange?: (keys: ReadonlySet<string>) => void;
  hasOlderMessages?: boolean;
  autoLoadBlockedSummaryKey?: string | null;
  onAutoLoadOlderMessages?: () => void;
}) {
  const ref = React.useRef<HTMLDivElement>(null);
  const [ready, setReady] = React.useState(false);
  React.useLayoutEffect(() => setReady(true), []);
  const items = React.useMemo(
    () => buildConversationRenderModel(history, { isCurrentTurnActive: active, historyTurns }),
    [history, historyTurns, active],
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
        onWorkSummaryExpansionChange: onExpansionChange,
        hasOlderMessages,
        autoLoadBlockedSummaryKey,
        onAutoLoadOlderMessages,
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

test("a paged completed turn starts closed and keeps expansion while earlier process arrives", async () => {
  const turnId = "turn-paged";
  const input = {
    ...messages[0],
    additionalProperties: { turnId },
  };
  const result = {
    ...messages[2],
    additionalProperties: { type: "result", turnId },
  };
  const late = {
    ...messages[1],
    messageId: "late-process",
    contents: [{ type: "TextContent", content: "Late work" }],
    additionalProperties: { turnId },
  };
  const early = {
    ...messages[1],
    messageId: "early-process",
    contents: [{ type: "TextContent", content: "Early work" }],
    additionalProperties: { turnId },
  };
  const historyTurns: ConversationHistoryTurn[] = [
    {
      turnId,
      status: "completed",
      input,
      results: [result],
      hasProcessMessages: true,
    },
  ];
  const observed: ReadonlySet<string>[] = [];
  const onExpansionChange = (keys: ReadonlySet<string>) => observed.push(new Set(keys));
  const view = render(
    React.createElement(Harness, { history: [late], historyTurns, onExpansionChange }),
  );
  const trigger = await screen.findByRole("button", { name: "Worked for 17m 39s" });
  assert.equal(trigger.getAttribute("aria-expanded"), "false");
  assert.ok(screen.getByText("Review the change"));
  assert.ok(screen.getByText("Review complete"));
  assert.equal(screen.queryByText("Late work"), null);
  assert.equal(observed.at(-1)?.size, 0);

  fireEvent.click(trigger);
  assert.equal(trigger.getAttribute("aria-expanded"), "true");
  assert.ok(screen.getByText("Late work"));
  assert.ok(observed.at(-1)?.has(`work-summary:${turnId}`));

  view.rerender(
    React.createElement(Harness, {
      history: [early, result, late],
      historyTurns,
      onExpansionChange,
    }),
  );
  assert.equal(
    screen.getByRole("button", { name: "Worked for 17m 39s" }).getAttribute("aria-expanded"),
    "true",
  );
  assert.ok(screen.getByText("Early work"));
  assert.ok(screen.getByText("Late work"));
  assert.equal(screen.getAllByText("Review complete").length, 1);

  view.rerender(
    React.createElement(Harness, {
      conversationKey: "next",
      history: [early, result, late],
      historyTurns,
      onExpansionChange,
    }),
  );
  assert.equal(
    screen.getByRole("button", { name: "Worked for 17m 39s" }).getAttribute("aria-expanded"),
    "false",
  );
  assert.equal(screen.queryByText("Late work"), null);
  assert.equal(observed.at(-1)?.size, 0);
});

test("short folded history requests another page after its work is expanded", async () => {
  const turnId = "short-turn";
  const history = [{ ...messages[1], additionalProperties: { turnId } }];
  const historyTurns: ConversationHistoryTurn[] = [
    {
      turnId,
      status: "completed",
      input: { ...messages[0], additionalProperties: { turnId } },
      results: [{ ...messages[2], additionalProperties: { type: "result", turnId } }],
      hasProcessMessages: true,
    },
  ];
  let requests = 0;
  render(
    React.createElement(Harness, {
      history,
      historyTurns,
      hasOlderMessages: true,
      autoLoadBlockedSummaryKey: `work-summary:${turnId}`,
      onAutoLoadOlderMessages: () => requests++,
    }),
  );
  await act(async () => new Promise((resolve) => setTimeout(resolve, 20)));
  assert.equal(requests, 0);

  fireEvent.click(await screen.findByRole("button", { name: "Worked for 17m 39s" }));
  await act(async () => new Promise((resolve) => setTimeout(resolve, 20)));
  assert.equal(requests, 1);
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
