import assert from "node:assert/strict";
import test from "node:test";
import { installLayoutMetrics, setupDomEnvironment } from "@agw/test-harness";

import type { ConversationRenderItem, PresentedMessage, PresentedTool } from "@agw/chat-core";
import type { InteractionResponse } from "@agw/execution-core";

const environment = await setupDomEnvironment();
const { React, act, fireEvent, render, screen, within } = environment;
installLayoutMetrics(environment.window);
const { Conversation } = await import("./conversation.tsx");

function textMessage(id: string, text: string, alignment: "left" | "right"): PresentedMessage {
  return {
    source: { messageId: id, role: alignment === "right" ? "user" : "assistant", contents: [] },
    identity: id,
    alignment,
    width: "normal",
    meta: null,
    contents: [{ type: "plain", text, sourceType: "TextContent" }],
  };
}

function messageItem(id: string, text: string, alignment: "left" | "right" = "left") {
  return {
    key: id,
    type: "message",
    alignment,
    width: "normal",
    message: textMessage(id, text, alignment),
  } as ConversationRenderItem;
}

function tool(name: string, summary: string, status: PresentedTool["status"]): PresentedTool {
  return { identity: name, scopeId: null, toolName: name, summary, status, messages: [] };
}

type ConversationOptions = {
  items?: ConversationRenderItem[];
  hasOlderMessages?: boolean;
  isLoadingOlderMessages?: boolean;
  isInitialLoading?: boolean;
  loadRequests?: number[];
  responses?: InteractionResponse[];
  resumes?: string[];
  showCheckpointResume?: boolean;
};

function Harness(options: ConversationOptions) {
  const scrollRef = React.useRef<HTMLDivElement>(null);
  const [ready, setReady] = React.useState(false);
  React.useLayoutEffect(() => setReady(true), []);

  return React.createElement(
    "div",
    { ref: scrollRef, "data-testid": "scroller" },
    ready &&
      React.createElement(Conversation, {
        items: options.items ?? [],
        scrollElementRef: scrollRef,
        hasOlderMessages: options.hasOlderMessages,
        isLoadingOlderMessages: options.isLoadingOlderMessages,
        isInitialLoading: options.isInitialLoading,
        onLoadOlderMessages: () => options.loadRequests?.push(1),
        showCheckpointResume: options.showCheckpointResume,
        onCheckpointResume: (occurrenceId: string) => options.resumes?.push(occurrenceId),
        onHumanResponse: (response: InteractionResponse) => options.responses?.push(response),
      }),
  );
}

function renderConversation(options: ConversationOptions = {}) {
  return render(React.createElement(Harness, options));
}

test("an empty conversation says there are no messages", async () => {
  renderConversation();

  assert.ok(await screen.findByText("No Message Yet"));
  assert.ok(screen.getByText("There are currently no messages."));
});

test("an empty conversation that is still loading says so instead", async () => {
  renderConversation({ isInitialLoading: true });

  assert.ok(await screen.findByText("Loading conversation..."));
  assert.equal(screen.queryByText("No Message Yet"), null);
});

test("messages render as a labelled list in order", async () => {
  renderConversation({
    items: [messageItem("m1", "Review the change", "right"), messageItem("m2", "Reviewing now")],
  });

  const list = await screen.findByRole("list", { name: "Conversation messages" });
  assert.deepEqual(
    within(list)
      .getAllByRole("listitem")
      .map((row) => row.textContent),
    ["Review the change", "Reviewing now"],
  );
});

test("older history offers a load action and reports the request", async () => {
  const loadRequests: number[] = [];
  renderConversation({
    items: [messageItem("m1", "Latest")],
    hasOlderMessages: true,
    loadRequests,
  });

  const action = await screen.findByRole("button", { name: "Load earlier messages" });
  fireEvent.click(action);

  assert.deepEqual(loadRequests, [1]);
});

test("a history load in progress disables its action", async () => {
  renderConversation({ items: [messageItem("m1", "Latest")], isLoadingOlderMessages: true });

  const action = await screen.findByRole("button", { name: /Loading earlier messages/ });
  assert.equal(action.hasAttribute("disabled"), true);
});

test("agent metadata renders name, author, and model", async () => {
  const message = textMessage("m1", "Reviewing now", "left");
  message.meta = { name: "Review Node", author: "claude-code", model: "claude-opus-5" };
  renderConversation({
    items: [{ key: "m1", type: "message", alignment: "left", width: "normal", message }],
  });

  assert.ok(await screen.findByText("Review Node"));
  assert.ok(screen.getByText("claude-code"));
  assert.equal(screen.getByText("claude-opus-5").getAttribute("title"), "claude-opus-5");
});

test("a tool run renders its name, summary, and status", async () => {
  renderConversation({
    items: [
      {
        key: "tool-1",
        type: "tool-accordion",
        alignment: "left",
        width: "normal",
        ...tool("command_execution", "dotnet build Agw.slnx", "complete"),
      } as ConversationRenderItem,
    ],
  });

  assert.ok(await screen.findByText("command_execution"));
  assert.ok(screen.getByText("dotnet build Agw.slnx"));
});

test("a tool batch summarizes its calls and status counts", async () => {
  renderConversation({
    items: [
      {
        key: "batch-1",
        type: "tool-batch",
        alignment: "left",
        width: "normal",
        tools: [
          tool("read_file", "src/index.ts", "complete"),
          tool("write_file", "src/output.ts", "failed"),
        ],
      } as ConversationRenderItem,
    ],
  });

  assert.ok(await screen.findByText("2 tool calls"));
  assert.match(screen.getByText(/read_file/).textContent ?? "", /write_file/);
});

test("a pending tool approval answers through the conversation callback", async () => {
  const responses: InteractionResponse[] = [];
  renderConversation({
    responses,
    items: [
      {
        key: "interaction-1",
        type: "human-interaction",
        alignment: "left",
        width: "normal",
        embedded: false,
        request: {
          kind: "tool-approval",
          interactionId: "interaction-1",
          prompt: "Run the migration script?",
          source: { toolName: "command_execution" },
        },
      } as ConversationRenderItem,
    ],
  });

  const approve = await screen.findByRole("button", { name: /Always allow tool/ });
  fireEvent.click(approve);
  fireEvent.click(screen.getByRole("button", { name: /Reject/ }));

  assert.deepEqual(responses, [
    { kind: "tool-approval", interactionId: "interaction-1", approved: true, scope: "AlwaysTool" },
    { kind: "tool-approval", interactionId: "interaction-1", approved: false, scope: "Once" },
  ]);
});

test("a pending user input announces its source and submits a response", async () => {
  const responses: InteractionResponse[] = [];
  renderConversation({
    responses,
    items: [
      {
        key: "interaction-2",
        type: "human-interaction",
        alignment: "left",
        width: "normal",
        embedded: false,
        request: {
          kind: "user-input",
          interactionId: "interaction-2",
          prompt: "Which target?",
          source: { toolName: "ask_user" },
          inputKind: "confirm",
          payload: {},
        },
      } as ConversationRenderItem,
    ],
  });

  assert.ok(await screen.findByText("ask_user"));
  assert.ok(screen.getByText("Waiting for your input"));
  fireEvent.click(screen.getByRole("button", { name: "Confirm" }));

  assert.deepEqual(responses, [
    {
      kind: "user-input",
      interactionId: "interaction-2",
      cancelled: false,
      responseData: { confirmed: true },
    },
  ]);
});

test("an answered question interaction renders its result", async () => {
  renderConversation({
    items: [
      {
        key: "result-1",
        type: "human-interaction-result",
        alignment: "left",
        width: "normal",
        result: {
          cancelled: false,
          items: [{ question: "Which database provider?", answer: "PostgreSQL" }],
        },
      } as ConversationRenderItem,
    ],
  });

  assert.ok(await screen.findByText("Which database provider?"));
  assert.ok(screen.getByText("PostgreSQL"));
});

test("an available checkpoint can resume its occurrence", async () => {
  const resumes: string[] = [];
  renderConversation({
    resumes,
    showCheckpointResume: true,
    items: [
      {
        key: "checkpoint-1",
        type: "checkpoint",
        alignment: "left",
        width: "normal",
        checkpoint: { occurrenceId: "occurrence-1", nodeId: "node-1", name: "After review" },
        availability: {
          occurrenceId: "occurrence-1",
          agentflowId: "flow-1",
          boundarySequence: 1,
          available: true,
          markers: [],
        },
      } as ConversationRenderItem,
    ],
  });

  assert.ok(await screen.findByText("After review"));
  fireEvent.click(screen.getByRole("button", { name: /Resume/ }));

  assert.deepEqual(resumes, ["occurrence-1"]);
});

test("user input navigation renders into the host the caller provides", async () => {
  function NavigationHarness() {
    const scrollRef = React.useRef<HTMLDivElement>(null);
    const [host, setHost] = React.useState<HTMLDivElement | null>(null);
    const items = Array.from({ length: 40 }, (_, index) =>
      messageItem(`m${index}`, `Message ${index}`, index % 8 === 0 ? "right" : "left"),
    );

    return React.createElement(
      React.Fragment,
      null,
      React.createElement("div", { ref: setHost, "data-testid": "navigation-host" }),
      React.createElement(
        "div",
        { ref: scrollRef, "data-testid": "scroller" },
        host &&
          React.createElement(Conversation, {
            items,
            scrollElementRef: scrollRef,
            userInputNavigationHost: host,
          }),
      ),
    );
  }

  const view = render(React.createElement(NavigationHarness));
  const scroller = await screen.findByTestId("scroller");
  Object.defineProperty(scroller, "clientHeight", { configurable: true, value: 400 });
  await act(async () => {
    fireEvent.scroll(scroller);
  });

  const host = view.container.querySelector('[data-testid="navigation-host"]');
  assert.ok(host);
  assert.ok(within(host as HTMLElement).getByRole("navigation", { name: "User input navigation" }));
});

const turnInputs = [
  { inputMessageId: "earlier", inputSummary: "Earlier request" },
  { inputMessageId: "latest", inputSummary: "Latest request" },
];

// 加载后的 "earlier" 前面有 5 行，它的起始位置是 5 × 72 = 360。
// Once loaded, "earlier" follows five rows, so it starts at 5 × 72 = 360.
function TurnNavigationHarness({
  loaded,
  requested,
  loadResult = true,
  conversationKey = "a",
}: {
  loaded: boolean;
  requested: string[];
  loadResult?: boolean;
  conversationKey?: string;
}) {
  const scrollRef = React.useRef<HTMLDivElement>(null);
  const [host, setHost] = React.useState<HTMLDivElement | null>(null);
  const items = [
    ...(loaded
      ? [
          ...Array.from({ length: 5 }, (_, index) =>
            messageItem(`prelude-${index}`, `Prelude ${index}`),
          ),
          messageItem("earlier", "Earlier request", "right"),
        ]
      : []),
    messageItem("latest", "Latest request", "right"),
    ...Array.from({ length: 20 }, (_, index) => messageItem(`reply-${index}`, `Reply ${index}`)),
  ];
  return React.createElement(
    React.Fragment,
    null,
    React.createElement("div", { ref: setHost }),
    React.createElement(
      "div",
      { ref: scrollRef, "data-testid": "scroller" },
      host &&
        React.createElement(Conversation, {
          items,
          conversationKey,
          scrollElementRef: scrollRef,
          userInputNavigationHost: host,
          userInputs: turnInputs,
          onLoadUserInput: async (key: string) => {
            requested.push(key);
            return loadResult;
          },
        }),
    ),
  );
}

test("only unloaded turn anchors request history", async () => {
  const requested: string[] = [];
  const view = render(React.createElement(TurnNavigationHarness, { loaded: false, requested }));
  fireEvent.click(
    await screen.findByRole("button", { name: "Jump to user input: Earlier request" }),
  );
  assert.deepEqual(requested, ["earlier"]);
  view.rerender(React.createElement(TurnNavigationHarness, { loaded: true, requested }));
  assert.ok(view.container.querySelector('[data-msg-id="earlier"]'));
  fireEvent.click(screen.getByRole("button", { name: "Jump to user input: Earlier request" }));
  assert.deepEqual(requested, ["earlier"]);
  view.rerender(
    React.createElement(TurnNavigationHarness, { loaded: false, requested, conversationKey: "b" }),
  );
  fireEvent.click(screen.getByRole("button", { name: "Jump to user input: Earlier request" }));
  assert.deepEqual(requested, ["earlier", "earlier"]);
});

for (const loadResult of [true, false]) {
  test(`a history load that ${loadResult ? "reaches" : "misses"} the selected input ${loadResult ? "jumps" : "does not jump"} once it appears`, async () => {
    const requested: string[] = [];
    const view = render(
      React.createElement(TurnNavigationHarness, { loaded: false, requested, loadResult }),
    );
    const button = await screen.findByRole("button", {
      name: "Jump to user input: Earlier request",
    });
    await act(async () => {
      fireEvent.click(button);
    });
    assert.deepEqual(requested, ["earlier"]);

    // 模拟用户在加载结束后停留在别处阅读，随后向上滚动加载到了目标输入。
    // The reader stays elsewhere after the load ends, then scrolling up loads the target input.
    const scroller = screen.getByTestId("scroller");
    Object.defineProperty(scroller, "scrollHeight", { configurable: true, value: 2000 });
    Object.defineProperty(scroller, "clientHeight", { configurable: true, value: 600 });
    scroller.scrollTop = 1000;
    await act(async () => {
      view.rerender(
        React.createElement(TurnNavigationHarness, { loaded: true, requested, loadResult }),
      );
    });

    assert.equal(scroller.scrollTop, loadResult ? 360 : 1000);
  });
}
