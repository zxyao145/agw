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

/** 没有 `extensionId` 时与旧版 Server 的响应相同。Without `extensionId` it matches an older Server's response. */
function conversationRecord(conversationId: string, title: string, extensionId?: string) {
  return {
    projectId: PROJECT_ID,
    conversationId,
    contextId: `context-${conversationId}`,
    title,
    executionCount: 1,
    messageCount: 2,
    createTime: "2026-10-17T00:00:00Z",
    ...(extensionId === undefined ? {} : { extensionId }),
  };
}

const api = await startApiServer({
  [`GET /api/projects/${PROJECT_ID}/conversations`]: {
    items: [
      conversationRecord("conversation-1", "First chat", "host"),
      conversationRecord("conversation-2", "Second chat", "sample"),
      conversationRecord("conversation-3", "Third chat"),
    ],
    total: 3,
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

/**
 * 标题前的创建方图标：名称与 lucide 图标的类名；没有图标时为 null。
 * The creator icon before a row's title: its name and lucide icon class; null without an icon.
 */
function creatorIcon(title: string): { name: string | null; icon: string | undefined } | null {
  const titleElement = screen.getByText(title);
  const row = titleElement.closest(".group") as HTMLElement;
  const image = within(row).queryByRole("img");
  if (image === null) return null;
  assert.ok(
    image.compareDocumentPosition(titleElement) & Node.DOCUMENT_POSITION_FOLLOWING,
    "the creator icon comes before the title",
  );
  const svg = image.querySelector("svg");
  assert.ok(svg !== null);
  return {
    name: image.getAttribute("aria-label"),
    icon: [...svg.classList].find(
      (name) => name === "lucide-message-circle-text" || name === "lucide-puzzle",
    ),
  };
}

test("each row shows its creator icon before the title", async () => {
  renderList();

  await screen.findByText("Third chat");
  assert.deepEqual(creatorIcon("First chat"), {
    name: "Created by Agw",
    icon: "lucide-message-circle-text",
  });
  assert.deepEqual(creatorIcon("Second chat"), {
    name: 'Created by extension "sample"',
    icon: "lucide-puzzle",
  });
  assert.equal(creatorIcon("Third chat"), null, "an older Server reports no creator");
});
