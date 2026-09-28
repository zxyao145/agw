import assert from "node:assert/strict";
import test from "node:test";
import { installLayoutMetrics, setupDomEnvironment } from "@agw/test-harness";

import type { AiMessage, ConversationHistoryTurn } from "@agw/api";

const environment = await setupDomEnvironment();
const { React, act, fireEvent, render, screen } = environment;
installLayoutMetrics(environment.window);
const { Conversation } = await import("./conversation");
const { buildConversationRenderModel, getUnloadedWorkSummaryKeys } = await import("@agw/chat-core");

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
  onAutoLoadOlderMessages,
}: {
  active?: boolean;
  conversationKey?: string;
  history?: AiMessage[];
  historyTurns?: ConversationHistoryTurn[];
  onToggle?: () => void;
  onExpansionChange?: (keys: ReadonlySet<string>) => void;
  hasOlderMessages?: boolean;
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
        autoLoadBlockedSummaryKeys: getUnloadedWorkSummaryKeys(items, history),
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

test("the work summary shows the Agent name before the duration", async () => {
  const history = [messages[0], messages[1], { ...messages[2], author: "claude-code" }];
  render(React.createElement(Harness, { history }));
  const trigger = await screen.findByRole("button", { name: "claude-code Worked for 17m 39s" });
  assert.equal(trigger.firstElementChild?.textContent, "claude-code");
});

test("the work summary prioritizes the node name over the Result's server author", async () => {
  const result = { ...messages[2], author: "$agw-server" };
  const process = { ...messages[1], additionalProperties: { nodeName: "commit" } };
  const view = render(React.createElement(Harness, { history: [messages[0], process, result] }));
  assert.ok(await screen.findByRole("button", { name: "commit Worked for 17m 39s" }));
  view.rerender(
    React.createElement(Harness, {
      history: [
        messages[0],
        messages[1],
        {
          ...result,
          additionalProperties: { type: "result", nodeName: "push", displayName: "Server" },
        },
      ],
    }),
  );
  assert.ok(await screen.findByRole("button", { name: "push Worked for 17m 39s" }));
  assert.equal(screen.queryByRole("button", { name: "$agw-server Worked for 17m 39s" }), null);
});

test("commit-push shows each Agent's work and Result in order with independent expansion", async () => {
  const history: AiMessage[] = [
    messages[0],
    {
      ...messages[1],
      messageId: "commit-process",
      contents: [{ type: "TextContent", content: "Committing changes" }],
    },
    {
      ...messages[2],
      messageId: "commit-result",
      createdAt: "2026-09-20T01:00:20Z",
      additionalProperties: { type: "result", nodeName: "commit" },
      contents: [{ type: "TextContent", content: "Commit complete" }],
    },
    {
      ...messages[1],
      messageId: "push-process",
      contents: [{ type: "TextContent", content: "Pushing changes" }],
    },
    {
      ...messages[2],
      messageId: "push-result",
      createdAt: "2026-09-20T01:00:46Z",
      additionalProperties: { type: "result", nodeName: "push" },
      contents: [{ type: "TextContent", content: "Push complete" }],
    },
  ];
  const view = render(React.createElement(Harness, { history: history.slice(0, 4), active: true }));
  const commit = await screen.findByRole("button", { name: "commit Worked for 20s" });
  assert.equal(commit.getAttribute("aria-expanded"), "false");
  assert.equal(screen.queryByText("Committing changes"), null);
  assert.ok(screen.getByText("Pushing changes"));
  fireEvent.click(commit);
  assert.ok(screen.getByText("Committing changes"));

  view.rerender(React.createElement(Harness, { history, active: true }));
  const push = await screen.findByRole("button", { name: "push Worked for 26s" });
  assert.equal(push.getAttribute("aria-expanded"), "false");
  assert.equal(screen.queryByText("Pushing changes"), null);
  assert.equal(
    screen.getByRole("button", { name: "commit Worked for 20s" }).getAttribute("aria-expanded"),
    "true",
  );
  assert.deepEqual(
    Array.from(
      view.container.querySelectorAll(
        '.agw-msg-result-collapse-btn, [data-msg-id="commit-result"], [data-msg-id="push-result"]',
      ),
    ).map((item) => item.getAttribute("data-msg-id") ?? item.textContent),
    ["commit Worked for 20s", "commit-result", "push Worked for 26s", "push-result"],
  );
  fireEvent.click(push);
  assert.ok(screen.getByText("Pushing changes"));
  fireEvent.click(screen.getByRole("button", { name: "commit Worked for 20s" }));
  assert.equal(screen.queryByText("Committing changes"), null);
  assert.ok(screen.getByText("Pushing changes"));
});

test("a paged completed turn starts closed and keeps expansion while earlier process arrives", async () => {
  const turnId = "turn-paged";
  const input = {
    ...messages[0],
    additionalProperties: { turnId, conversationSequence: 0 },
  };
  const result = {
    ...messages[2],
    additionalProperties: {
      type: "result",
      turnId,
      conversationSequence: 3,
      hasPrecedingProcessMessages: true,
    },
  };
  const late = {
    ...messages[1],
    messageId: "late-process",
    contents: [{ type: "TextContent", content: "Late work" }],
    additionalProperties: { turnId, conversationSequence: 2 },
  };
  const early = {
    ...messages[1],
    messageId: "early-process",
    contents: [{ type: "TextContent", content: "Early work" }],
    additionalProperties: { turnId, conversationSequence: 1 },
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
  const summary = buildConversationRenderModel([late], { historyTurns }).find(
    (item) => item.type === "work-summary",
  )!;
  assert.ok(observed.at(-1)?.has(summary.key));

  view.rerender(
    React.createElement(Harness, {
      history: [early, late, result],
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
      history: [early, late, result],
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
      onAutoLoadOlderMessages: () => requests++,
    }),
  );
  await act(async () => new Promise((resolve) => setTimeout(resolve, 20)));
  assert.equal(requests, 0);

  fireEvent.click(await screen.findByRole("button", { name: "Worked for 17m 39s" }));
  await act(async () => new Promise((resolve) => setTimeout(resolve, 20)));
  assert.equal(requests, 1);
});

test("expanding paged Agent work loads through its own starting boundary", async () => {
  const turnId = "paged-flow";
  const history: AiMessage[] = [
    messages[0],
    { ...messages[1], messageId: "commit-process" },
    {
      ...messages[2],
      messageId: "commit-result",
      createdAt: "2026-09-20T01:00:20Z",
      additionalProperties: {
        type: "result",
        nodeName: "commit",
        hasPrecedingProcessMessages: true,
      },
    },
    {
      ...messages[1],
      messageId: "push-process",
      contents: [{ type: "TextContent", content: "Pushing changes" }],
    },
    {
      ...messages[2],
      messageId: "push-result",
      createdAt: "2026-09-20T01:00:46Z",
      additionalProperties: { type: "result", nodeName: "push", hasPrecedingProcessMessages: true },
    },
  ].map((message, conversationSequence) => ({
    ...message,
    additionalProperties: { ...message.additionalProperties, turnId, conversationSequence },
  }));
  const historyTurns: ConversationHistoryTurn[] = [
    {
      turnId,
      status: "completed",
      input: history[0],
      results: [history[2], history[4]],
      hasProcessMessages: true,
    },
  ];
  let requests = 0;
  const onAutoLoadOlderMessages = () => requests++;
  const props = { historyTurns, hasOlderMessages: true, onAutoLoadOlderMessages };
  const view = render(React.createElement(Harness, { ...props, history: history.slice(3) }));
  await act(async () => new Promise((resolve) => setTimeout(resolve, 20)));
  assert.equal(requests, 0);
  fireEvent.click(await screen.findByRole("button", { name: "push Worked for 26s" }));
  await act(async () => new Promise((resolve) => setTimeout(resolve, 20)));
  assert.equal(requests, 1);

  view.rerender(React.createElement(Harness, { ...props, history: history.slice(2) }));
  await act(async () => new Promise((resolve) => setTimeout(resolve, 20)));
  assert.equal(requests, 1);
  assert.equal(
    screen.getByRole("button", { name: "push Worked for 26s" }).getAttribute("aria-expanded"),
    "true",
  );
  fireEvent.click(screen.getByRole("button", { name: "commit Worked for 20s" }));
  await act(async () => new Promise((resolve) => setTimeout(resolve, 20)));
  assert.equal(requests, 2);
});

test("expansion survives rerenders, virtual unmounts and resumed turns, and resets for new conversations", async () => {
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
  assert.equal(
    screen.getByRole("button", { name: "Worked for 17m 39s" }).getAttribute("aria-expanded"),
    "true",
  );
  assert.ok(screen.getByText("Checking implementation"));
  view.rerender(React.createElement(Harness));
  assert.equal(
    screen.getByRole("button", { name: "Worked for 17m 39s" }).getAttribute("aria-expanded"),
    "true",
  );
  fireEvent.click(screen.getByRole("button", { name: "Worked for 17m 39s" }));
  view.rerender(React.createElement(Harness, { conversationKey: "b" }));
  assert.equal(screen.queryByText("Checking implementation"), null);
});
