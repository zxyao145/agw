import assert from "node:assert/strict";
import test, { afterEach } from "node:test";
import { setupDomEnvironment, startApiServer } from "@agw/test-harness";

import type { ConversationItemActionArgs, ExtensionMenuItem } from "../../../lib/extension-menu";
import type { ConversationSummary } from "../../../services/task-client";

const { React, act, fireEvent, render, screen, within } = await setupDomEnvironment();
const { configureApiRuntime, resetApiRuntime } = await import("@agw/api");
const { QueryClient, QueryClientProvider } = await import("@agw/components/query");
const { TooltipProvider } = await import("@agw/components");
const { ConversationList } = await import("./conversation-list.tsx");

const PROJECT_ID = "project-1";

function conversationRecord(conversationId: string, title: string) {
  return {
    projectId: PROJECT_ID,
    conversationId,
    contextId: `context-${conversationId}`,
    title,
    executionCount: 1,
    messageCount: 2,
    createTime: "2026-10-17T00:00:00Z",
  };
}

const api = await startApiServer({
  [`GET /api/projects/${PROJECT_ID}/conversations`]: {
    items: [
      conversationRecord("conversation-1", "First chat"),
      conversationRecord("conversation-2", "Second chat"),
    ],
    total: 2,
    pageIndex: 1,
    pageSize: 20,
  },
});
configureApiRuntime({ baseUrl: api.baseUrl, token: null });
test.after(() => resetApiRuntime());

// 对话列表的查询自带 30 分钟回收计时器，不清空查询缓存时测试进程无法退出。
// The conversation query sets its own 30-minute garbage-collection timer, which keeps the test process alive unless the cache is cleared.
let client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
afterEach(() => client.clear());

function renderList(
  itemActions?: ExtensionMenuItem<ConversationItemActionArgs>[],
  selected: ConversationSummary[] = [],
) {
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    React.createElement(
      QueryClientProvider,
      { client },
      React.createElement(
        TooltipProvider,
        null,
        React.createElement(ConversationList, {
          projectId: PROJECT_ID,
          currentConversationId: null,
          onConversationSelect: (conversation: ConversationSummary) => selected.push(conversation),
          onNewConversation() {},
          onAllConversationsDeleted() {},
          itemActions,
        }),
      ),
    ),
  );
}

/** 对话行内按钮的名称，按显示顺序。The names of a conversation row's buttons, in display order. */
function rowButtonNames(title: string): (string | null)[] {
  const row = screen.getByText(title).closest(".group");
  assert.ok(row !== null);
  return within(row as HTMLElement)
    .getAllByRole("button")
    .map((button) => button.getAttribute("aria-label"));
}

test("item actions open from a More menu after Rename and Delete and receive the conversation", async () => {
  const selections: ConversationItemActionArgs[] = [];
  const selected: ConversationSummary[] = [];
  renderList(
    [
      {
        id: "extension:summarize",
        title: "Summarize",
        icon: React.createElement("svg", { "data-testid": "summarize icon" }),
        onSelect: (args) => selections.push(args),
      },
    ],
    selected,
  );

  await screen.findByText("Second chat");
  assert.deepEqual(rowButtonNames("Second chat"), [
    "Rename conversation",
    "Delete conversation",
    "More conversation actions",
  ]);

  const row = screen.getByText("Second chat").closest(".group") as HTMLElement;
  const trigger = within(row).getByRole("button", { name: "More conversation actions" });
  await act(async () => {
    fireEvent.pointerDown(trigger, { button: 0, ctrlKey: false, pointerType: "mouse" });
    fireEvent.click(trigger);
  });
  const menu = await screen.findByRole("menu");
  assert.equal(within(menu).queryByTestId("summarize icon") !== null, true);
  await act(async () => {
    fireEvent.click(within(menu).getByText("Summarize"));
  });

  assert.deepEqual(selections, [{ conversationId: "conversation-2", title: "Second chat" }]);
  assert.deepEqual(
    selected,
    [],
    "opening the menu or choosing an item does not open the conversation",
  );
});

test("without item actions a row keeps only Rename and Delete", async () => {
  renderList();

  await screen.findByText("First chat");
  assert.deepEqual(rowButtonNames("First chat"), ["Rename conversation", "Delete conversation"]);
  assert.equal(screen.queryByRole("button", { name: "More conversation actions" }), null);
});

test("an empty item action list shows no More menu", async () => {
  renderList([]);

  await screen.findByText("First chat");
  assert.equal(screen.queryByRole("button", { name: "More conversation actions" }), null);
});
