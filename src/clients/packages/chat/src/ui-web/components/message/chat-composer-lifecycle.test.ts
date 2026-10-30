import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";

import type { ChatProps } from "./chat";

const { React, fireEvent, render, screen } = await setupDomEnvironment();
const { QueryClient, QueryClientProvider } = await import("@agw/components/query");
const { Chat } = await import("./chat.tsx");

for (const [name, previousId, nextId] of [
  ["new chat", "conversation-a", null],
  ["another new chat", null, null],
  ["new session", "conversation-a", "conversation-a"],
  ["another conversation", "conversation-a", "conversation-b"],
] as const) {
  test(`${name} starts collapsed and allows the user to expand again`, (t) => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
    t.after(() => client.clear());
    const chat = (conversationId: string | null, revision: number) => {
      const props: ChatProps = {
        target: null,
        projectId: null,
        conversationId,
        sessionSeed: {
          revision,
          conversationId,
          messages: [],
          historyTurns: [],
          olderMessagesCursor: null,
          hasOlderMessages: false,
          agentMode: null,
        },
      };
      return React.createElement(QueryClientProvider, { client }, React.createElement(Chat, props));
    };
    const view = render(chat(previousId, 1));
    const textarea = screen.getByRole("combobox", {
      name: "",
    }) as HTMLTextAreaElement;
    fireEvent.change(textarea, { target: { value: "previous draft" } });
    fireEvent.click(screen.getByRole("button", { name: "Expand input" }));

    view.rerender(chat(nextId, 2));

    assert.equal(view.container.querySelector("textarea"), textarea);
    assert.equal(textarea.value, "");
    assert.ok(screen.getByRole("button", { name: "Expand input" }));
    fireEvent.click(screen.getByRole("button", { name: "Expand input" }));
    assert.ok(screen.getByRole("button", { name: "Collapse input" }));
  });
}
