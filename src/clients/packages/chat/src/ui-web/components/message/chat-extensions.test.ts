import assert from "node:assert/strict";
import test from "node:test";
import { installLayoutMetrics, setupDomEnvironment } from "@agw/test-harness";

import type { AiMessage } from "@agw/api";
import type { ChatProps } from "./chat";
import type {
  ChatComposerHandle,
  ChatMessageActionArgs,
  ExtensionMenuItem,
} from "./chat-extension-types";

const environment = await setupDomEnvironment();
const { React, act, fireEvent, render, screen, waitFor } = environment;
installLayoutMetrics(environment.window);
const { EMPTY_TOKEN_USAGE } = await import("@agw/api");
const { QueryClient, QueryClientProvider } = await import("@agw/components/query");
const { Chat } = await import("./chat.tsx");

const HISTORY: AiMessage[] = [
  {
    messageId: "user-1",
    role: "user",
    contents: [{ type: "TextContent", content: "Review the change" }],
  },
  {
    messageId: "result-1",
    role: "assistant",
    additionalProperties: { type: "result", turnId: "turn-1" },
    contents: [{ type: "TextContent", content: "**Done**" }],
  },
];

function renderChat(props: Partial<ChatProps> = {}, messages: AiMessage[] = []) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  return render(
    React.createElement(
      QueryClientProvider,
      { client },
      React.createElement(Chat, {
        target: null,
        projectId: null,
        conversationId: null,
        sessionSeed: {
          revision: 1,
          conversationId: null,
          messages,
          historyTurns: [],
          usage: EMPTY_TOKEN_USAGE,
          olderMessagesCursor: null,
          hasOlderMessages: false,
          agentMode: null,
        },
        ...props,
      }),
    ),
  );
}

async function openMenu(trigger: HTMLElement) {
  await act(async () => {
    fireEvent.pointerDown(trigger, { button: 0, ctrlKey: false, pointerType: "mouse" });
  });
  return screen.findByRole("menu");
}

// 关闭的菜单在下一个任务中把焦点还给触发按钮；之前打开的下一个菜单会被这次焦点移动关闭。
// A closed menu returns focus to its trigger in the next task; a menu opened earlier would be dismissed by that focus move.
async function selectMenuItem(title: string) {
  await act(async () => {
    fireEvent.click(screen.getByText(title));
  });
  await act(() => new Promise((resolve) => setTimeout(resolve, 0)));
}

function menuItem<TArgs>(title: string, selections: TArgs[]): ExtensionMenuItem<TArgs> {
  return {
    id: `extension:${title}`,
    title,
    icon: React.createElement("svg", { "data-testid": `${title} icon` }),
    onSelect: (args) => selections.push(args),
  };
}

test("composer actions form an Extensions group in the Add menu and report their selection", async () => {
  const selections: Record<string, never>[] = [];
  renderChat({ composerActions: [menuItem("Insert snippet", selections)] });

  const menu = await openMenu(screen.getByRole("button", { name: "Add" }));
  assert.equal(menu.textContent?.includes("Extensions"), true);
  assert.equal(screen.queryByText("No actions are available for this target."), null);
  assert.equal(screen.queryByTestId("Insert snippet icon") !== null, true);

  await selectMenuItem("Insert snippet");
  assert.deepEqual(selections, [{}]);
});

test("composerRef inserts text at the caret and refocuses the composer", async () => {
  const composerRef = React.createRef<ChatComposerHandle>();
  const view = renderChat({ composerRef });
  const textarea = view.container.querySelector("textarea")!;

  fireEvent.change(textarea, { target: { value: "Hello world" } });
  textarea.setSelectionRange(6, 6);
  textarea.blur();
  act(() => composerRef.current!.insertText("@README.md"));

  assert.equal(textarea.value, "Hello @README.md world");
  await waitFor(() => assert.equal(environment.window.document.activeElement === textarea, true));
  assert.equal(textarea.selectionStart, "Hello @README.md ".length);
});

test("message More menus pass the message ID, role, copy text and turn", async () => {
  const selections: ChatMessageActionArgs[] = [];
  renderChat({ messageActions: [menuItem("Send to extension", selections)] }, HISTORY);

  await screen.findByText("Review the change");
  const triggers = screen.getAllByRole("button", { name: "More message actions" });
  assert.equal(triggers.length, 2, "the user message and the result both offer the menu");
  for (const trigger of triggers) {
    await openMenu(trigger);
    await selectMenuItem("Send to extension");
  }

  assert.deepEqual(selections, [
    { messageId: "user-1", role: "user", text: "Review the change", turnId: null },
    { messageId: "result-1", role: "assistant", text: "**Done**", turnId: "turn-1" },
  ]);
});

test("without extension props Chat shows neither the Extensions group nor More menus", async () => {
  renderChat({}, HISTORY);

  await screen.findByText("Review the change");
  assert.equal(screen.getAllByRole("button", { name: "Copy message" }).length, 2);
  assert.equal(screen.queryByRole("button", { name: "More message actions" }), null);
  const menu = await openMenu(screen.getByRole("button", { name: "Add" }));
  assert.equal(menu.textContent?.includes("Extensions"), false);
  assert.equal(screen.queryByText("No actions are available for this target.") !== null, true);
});
