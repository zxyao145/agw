import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";

import type { SuggestionItem } from "@agw/chat-core";

const { React, act, cleanup, createEvent, fireEvent, render, screen, waitFor } =
  await setupDomEnvironment();
const { UserInput } = await import("./user-input.tsx");

const suggestions: SuggestionItem[] = [
  { text: "/alpha", description: "First suggestion" },
  { text: "/beta", description: "Second suggestion" },
  { text: "/gamma", description: "Third suggestion" },
];

function renderComposer(
  onSuggestion: (value: string, caretIndex: number) => SuggestionItem[] | Promise<SuggestionItem[]>,
  onExecute: (value: string) => void = () => {},
) {
  render(
    React.createElement(UserInput, {
      onSuggestion,
      onExecute: (value: string) => {
        onExecute(value);
        return true;
      },
    }),
  );
  const textarea = screen.getByRole("combobox") as HTMLTextAreaElement;
  textarea.focus();
  fireEvent.change(textarea, { target: { value: "/x", selectionStart: 2 } });
  return textarea;
}

test("arrow keys wrap through suggestions and Enter inserts the active item without sending", async () => {
  const sent: string[] = [];
  const textarea = renderComposer(
    () => suggestions,
    (value) => sent.push(value),
  );
  const options = screen.getAllByRole("option");

  assert.equal(options[0].getAttribute("aria-selected"), "true");
  assert.equal(textarea.getAttribute("aria-expanded"), "true");
  assert.equal(textarea.getAttribute("aria-activedescendant"), options[0].getAttribute("id"));
  assert.match(options[0].className, /bg-accent\/50/);

  assert.equal(fireEvent.keyDown(textarea, { key: "ArrowUp" }), false);
  assert.equal(options[2].getAttribute("aria-selected"), "true");
  fireEvent.keyDown(textarea, { key: "ArrowDown" });
  assert.equal(options[0].getAttribute("aria-selected"), "true");
  fireEvent.keyDown(textarea, { key: "ArrowDown" });
  assert.equal(options[1].getAttribute("aria-selected"), "true");
  assert.strictEqual(document.activeElement, textarea);

  assert.equal(fireEvent.keyDown(textarea, { key: "Enter" }), false);
  await waitFor(() => assert.strictEqual(document.activeElement, textarea));
  assert.equal(textarea.value, "/beta ");
  assert.equal(textarea.selectionStart, textarea.value.length);
  assert.equal(screen.queryByRole("listbox"), null);
  assert.deepEqual(sent, []);
});

test("Tab inserts the active suggestion without sending", async () => {
  const sent: string[] = [];
  const textarea = renderComposer(
    () => suggestions,
    (value) => sent.push(value),
  );

  fireEvent.keyDown(textarea, { key: "ArrowDown" });
  assert.equal(fireEvent.keyDown(textarea, { key: "Tab" }), false);
  await waitFor(() => assert.strictEqual(document.activeElement, textarea));
  assert.equal(textarea.value, "/beta ");
  assert.equal(textarea.selectionStart, textarea.value.length);
  assert.equal(screen.queryByRole("listbox"), null);
  assert.deepEqual(sent, []);
});

test("mouse selection inserts the clicked suggestion and restores the caret", async () => {
  const textarea = renderComposer(() => suggestions);

  fireEvent.click(screen.getAllByRole("option")[2]);

  await waitFor(() => assert.strictEqual(document.activeElement, textarea));
  assert.equal(textarea.value, "/gamma ");
  assert.equal(textarea.selectionStart, textarea.value.length);
  assert.equal(screen.queryByRole("listbox"), null);
});

test("file suggestions show the name and relative directory on one line", () => {
  renderComposer(() => [
    {
      text: "@/Users/ben/source/repos/agw/deploy/k8s/README.md",
      description: "deploy/k8s/README.md",
    },
  ]);
  const option = screen.getByRole("option");
  const content = option.querySelector('[data-slot="item-content"]');

  assert.equal(content?.textContent?.trim(), "README.mddeploy/k8s");
  assert.match(content?.className ?? "", /flex-row/);
  assert.equal(content?.querySelector("p"), null);
  assert.doesNotMatch(content?.textContent ?? "", /@\/Users\/ben/);
});

test("slash suggestions share the single-line layout with type, name, and full description", () => {
  const description = "Tool · Interaction · " + "Long description ".repeat(30);
  const textarea = renderComposer(() => [
    { text: "/ask_user_question", kind: "tool", description },
    { text: "/commit", kind: "skill", description: "Create a commit" },
    { text: "/compact" },
  ]);
  const options = screen.getAllByRole("option");
  for (const option of options) {
    const content = option.querySelector('[data-slot="item-content"]');
    assert.match(content?.className ?? "", /flex-row/);
    assert.match(content?.className ?? "", /whitespace-nowrap/);
    assert.equal(content?.querySelector("p"), null);
  }
  const content = options[0].querySelector('[data-slot="item-content"]')!;
  assert.deepEqual(
    Array.from(content.children, (child) => child.textContent),
    ["tool", "/ask_user_question", description],
  );
  assert.equal(content.lastElementChild?.getAttribute("title"), description);
  assert.match(content.lastElementChild?.className ?? "", /truncate/);
  assert.equal(options[1].firstElementChild?.firstElementChild?.textContent, "skill");
  assert.equal(options[2].firstElementChild?.firstElementChild?.textContent, "command");
  fireEvent.keyDown(textarea, { key: "Enter" });
  assert.equal(textarea.value, "/ask_user_question ");
});

test("new filtered results select the first option again", () => {
  let currentSuggestions = suggestions;
  const textarea = renderComposer(() => currentSuggestions);

  fireEvent.keyDown(textarea, { key: "ArrowDown" });
  assert.equal(screen.getAllByRole("option")[1].getAttribute("aria-selected"), "true");

  currentSuggestions = [{ text: "/delta" }, { text: "/debug" }];
  fireEvent.change(textarea, { target: { value: "/d", selectionStart: 2 } });

  const options = screen.getAllByRole("option");
  assert.ok(options[0].contains(screen.getByText("/delta")));
  assert.equal(options[0].getAttribute("aria-selected"), "true");
  assert.equal(options[1].getAttribute("aria-selected"), "false");
});

test("stale asynchronous suggestions cannot replace newer results", async () => {
  const resolvers = new Map<string, (value: SuggestionItem[]) => void>();
  const textarea = renderComposer(
    (value) =>
      new Promise<SuggestionItem[]>((resolve) => {
        resolvers.set(value, resolve);
      }),
  );

  fireEvent.change(textarea, { target: { value: "/a", selectionStart: 2 } });
  fireEvent.change(textarea, { target: { value: "/b", selectionStart: 2 } });
  await act(async () => resolvers.get("/b")?.([{ text: "/beta" }]));
  assert.ok(screen.getByRole("option").contains(screen.getByText("/beta")));

  await act(async () => resolvers.get("/a")?.([{ text: "/alpha" }]));
  assert.ok(screen.getByRole("option").contains(screen.getByText("/beta")));
  assert.equal(screen.getByRole("option").getAttribute("aria-selected"), "true");
});

test("IME composition does not select or confirm a suggestion", () => {
  const sent: string[] = [];
  const textarea = renderComposer(
    () => suggestions,
    (value) => sent.push(value),
  );
  const enter = createEvent.keyDown(textarea, { key: "Enter", isComposing: true });
  fireEvent(textarea, enter);
  const arrowDown = createEvent.keyDown(textarea, { key: "ArrowDown", isComposing: true });
  fireEvent(textarea, arrowDown);

  assert.equal(enter.defaultPrevented, false);
  assert.equal(arrowDown.defaultPrevented, false);
  assert.equal(screen.getAllByRole("option")[0].getAttribute("aria-selected"), "true");
  assert.ok(screen.getByRole("listbox"));
  assert.deepEqual(sent, []);
});

test("Ctrl+Enter and Shift+Enter still send while suggestions are open", () => {
  for (const modifier of ["ctrlKey", "shiftKey"] as const) {
    const sent: string[] = [];
    const textarea = renderComposer(
      () => suggestions,
      (value) => sent.push(value),
    );

    assert.equal(fireEvent.keyDown(textarea, { key: "Enter", [modifier]: true }), false);
    assert.deepEqual(sent, ["/x"]);
    assert.equal(screen.queryByRole("listbox"), null);
    cleanup();
  }
});

test("Enter without suggestions remains available for a newline", () => {
  const sent: string[] = [];
  const textarea = renderComposer(
    () => [],
    (value) => sent.push(value),
  );

  assert.equal(fireEvent.keyDown(textarea, { key: "Enter" }), true);
  assert.deepEqual(sent, []);
});
