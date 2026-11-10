import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";

import type { UserInputRef } from "./user-input";

const { React, act, fireEvent, render, screen, window } = await setupDomEnvironment();
const { UserInput } = await import("./user-input.tsx");

type ComposerOptions = {
  executed?: string[];
  values?: string[];
  stops?: number[];
  isExecuting?: boolean;
  isDisabled?: boolean;
  isSubmitDisabled?: boolean;
  canSubmitWithoutText?: boolean;
  /** 提交是否被受理；默认受理。Whether a submission is accepted; accepted by default. */
  accept?: boolean;
  onStop?: boolean;
  children?: unknown;
};

function renderComposer(options: ComposerOptions = {}) {
  const ref = React.createRef<UserInputRef>();
  const view = render(
    React.createElement(
      UserInput,
      {
        ref,
        expandOverlay: true,
        isExecuting: options.isExecuting,
        isDisabled: options.isDisabled,
        isSubmitDisabled: options.isSubmitDisabled,
        canSubmitWithoutText: options.canSubmitWithoutText,
        onExecute: (value: string) => {
          options.executed?.push(value);
          return options.accept ?? true;
        },
        onValueChange: (value: string) => options.values?.push(value),
        ...(options.onStop ? { onStop: () => options.stops?.push(1) } : {}),
      },
      options.children as never,
    ),
  );
  return { ref, view };
}

function composer() {
  return screen.getByRole("combobox") as HTMLTextAreaElement;
}

function sendButton() {
  const buttons = screen.getAllByRole("button");
  return buttons[buttons.length - 1];
}

function type(value: string) {
  fireEvent.change(composer(), { target: { value } });
}

test("the composer starts as one empty line with the send action disabled", () => {
  renderComposer();

  assert.equal(composer().value, "");
  assert.equal(composer().rows, 1);
  assert.equal(sendButton().hasAttribute("disabled"), true);
});

test("typing text enables sending and clears the draft after execution", () => {
  const executed: string[] = [];
  renderComposer({ executed });

  type("review the change");
  assert.equal(sendButton().hasAttribute("disabled"), false);
  fireEvent.click(sendButton());

  assert.deepEqual(executed, ["review the change"]);
  assert.equal(composer().value, "");
});

test("Enter sends from the collapsed composer", () => {
  const executed: string[] = [];
  renderComposer({ executed });

  type("send with Enter");
  assert.equal(fireEvent.keyDown(composer(), { key: "Enter" }), false);

  assert.deepEqual(executed, ["send with Enter"]);
  assert.equal(composer().value, "");
});

test("the expanded composer keeps Enter combinations as editing actions", () => {
  const executed: string[] = [];
  renderComposer({ executed });
  fireEvent.click(screen.getByRole("button", { name: "Expand input" }));

  for (const modifiers of [
    {},
    { shiftKey: true },
    { ctrlKey: true },
    { ctrlKey: true, shiftKey: true },
  ]) {
    type("draft");
    fireEvent.keyDown(composer(), { key: "Enter", ...modifiers });
    assert.deepEqual(executed, []);
    assert.ok(screen.getByRole("button", { name: "Collapse input" }));
  }
});

test("expanding and collapsing preserve the draft, selection, context, and toolbar", () => {
  const executed: string[] = [];
  renderComposer({
    executed,
    children: [
      React.createElement(UserInput.Context, { key: "context", children: "attached image" }),
      React.createElement(UserInput.BottomLeft, { key: "bottom", children: "toolbar" }),
    ],
  });
  const textarea = composer();
  type("keep this draft");
  textarea.setSelectionRange(5, 9);

  for (const name of ["Expand input", "Collapse input"]) {
    const toggle = screen.getByRole("button", { name });
    assert.equal(toggle.getAttribute("aria-expanded"), String(name === "Collapse input"));
    fireEvent.click(toggle);

    assert.equal(composer() === textarea, true);
    assert.equal(textarea.value, "keep this draft");
    assert.equal(textarea.selectionStart, 5);
    assert.equal(textarea.selectionEnd, 9);
    assert.equal(window.document.activeElement, textarea);
    assert.ok(screen.getByText("attached image"));
    assert.ok(screen.getByText("toolbar"));
  }

  fireEvent.keyDown(textarea, { key: "Enter" });
  assert.deepEqual(executed, ["keep this draft"]);
});

test("Shift+Enter inserts a newline and Ctrl+Enter sends from the collapsed composer", () => {
  const executed: string[] = [];
  renderComposer({ executed });

  type("draft");
  assert.equal(fireEvent.keyDown(composer(), { key: "Enter", shiftKey: true }), true);
  assert.deepEqual(executed, []);
  fireEvent.keyDown(composer(), { key: "Enter", ctrlKey: true });
  assert.deepEqual(executed, ["draft"]);
});

test("an in-progress composition does not send", () => {
  const executed: string[] = [];
  renderComposer({ executed });

  type("中文");
  const event = new window.KeyboardEvent("keydown", {
    key: "Enter",
    ctrlKey: true,
    bubbles: true,
  });
  Object.defineProperty(event, "isComposing", { value: true });
  act(() => {
    composer().dispatchEvent(event);
  });

  assert.deepEqual(executed, []);
});

test("plain Enter during IME composition or keyCode 229 keeps the collapsed draft", () => {
  const executed: string[] = [];
  renderComposer({ executed });
  type("中文");

  for (const composition of [{ isComposing: true }, { keyCode: 229 }]) {
    const event = new window.KeyboardEvent("keydown", {
      key: "Enter",
      bubbles: true,
      cancelable: true,
      ...composition,
    });
    act(() => {
      composer().dispatchEvent(event);
    });
    assert.equal(event.defaultPrevented, false);
  }

  assert.deepEqual(executed, []);
  assert.equal(composer().value, "中文");
});

test("code comments alone can be submitted without text", () => {
  const executed: string[] = [];
  renderComposer({ executed, canSubmitWithoutText: true });

  assert.equal(sendButton().hasAttribute("disabled"), false);
  fireEvent.click(sendButton());

  assert.deepEqual(executed, [""]);
});

test("blank text is empty content and is not submitted", () => {
  const executed: string[] = [];
  renderComposer({ executed });

  type("   \n  ");
  assert.equal(sendButton().hasAttribute("disabled"), true);
  fireEvent.keyDown(composer(), { key: "Enter", ctrlKey: true });

  assert.deepEqual(executed, []);
});

test("a rejected submission keeps the draft and expanded state", () => {
  const executed: string[] = [];
  const values: string[] = [];
  renderComposer({ executed, values, accept: false });
  fireEvent.click(screen.getByRole("button", { name: "Expand input" }));

  type("keep me");
  fireEvent.click(sendButton());
  fireEvent.keyDown(composer(), { key: "Enter", shiftKey: true });

  assert.deepEqual(executed, ["keep me"]);
  assert.equal(composer().value, "keep me");
  assert.deepEqual(values, ["keep me"]);
  assert.ok(screen.getByRole("button", { name: "Collapse input" }));
});

test("an accepted submission clears the draft, collapses the input and keeps focus", () => {
  renderComposer();
  fireEvent.click(screen.getByRole("button", { name: "Expand input" }));

  type("queued");
  fireEvent.click(sendButton());

  assert.equal(composer().value, "");
  assert.equal(window.document.activeElement, composer());
  assert.ok(screen.getByRole("button", { name: "Expand input" }));
});

test("a submit-only block keeps the textarea editable", () => {
  const executed: string[] = [];
  renderComposer({ executed, isSubmitDisabled: true });

  type("still editable");

  assert.equal(composer().value, "still editable");
  assert.equal(composer().hasAttribute("disabled"), false);
  assert.equal(sendButton().hasAttribute("disabled"), true);
  fireEvent.keyDown(composer(), { key: "Enter", ctrlKey: true });
  assert.deepEqual(executed, []);
});

test("a disabled composer blocks both editing and sending", () => {
  renderComposer({ isDisabled: true, canSubmitWithoutText: true });

  assert.equal(composer().hasAttribute("disabled"), true);
  assert.equal(sendButton().hasAttribute("disabled"), true);
});

test("a running execution without submittable content offers a stop request", () => {
  const executed: string[] = [];
  const stops: number[] = [];
  renderComposer({ executed, stops, isExecuting: true, onStop: true });

  assert.equal(composer().hasAttribute("disabled"), false);
  assert.equal(sendButton().getAttribute("aria-label"), "Stop execution");
  fireEvent.click(sendButton());

  assert.deepEqual(stops, [1]);
  assert.deepEqual(executed, []);
});

test("typing during a running execution turns the action back into sending", () => {
  const executed: string[] = [];
  const stops: number[] = [];
  renderComposer({ executed, stops, isExecuting: true, onStop: true });

  type("next step");
  assert.equal(sendButton().getAttribute("aria-label"), "Send message");
  fireEvent.click(sendButton());
  type("then this");
  fireEvent.keyDown(composer(), { key: "Enter", ctrlKey: true });

  assert.deepEqual(executed, ["next step", "then this"]);
  assert.deepEqual(stops, []);
  assert.equal(sendButton().getAttribute("aria-label"), "Stop execution");
});

test("code comments during a running execution are sent instead of stopping", () => {
  const executed: string[] = [];
  const stops: number[] = [];
  renderComposer({ executed, stops, isExecuting: true, onStop: true, canSubmitWithoutText: true });

  fireEvent.click(sendButton());

  assert.deepEqual(executed, [""]);
  assert.deepEqual(stops, []);
});

test("the shortcut never stops a running execution", () => {
  const stops: number[] = [];
  renderComposer({ stops, isExecuting: true, onStop: true });

  fireEvent.keyDown(composer(), { key: "Enter", ctrlKey: true });

  assert.deepEqual(stops, []);
});

test("a running execution without a stop handler offers no action", () => {
  renderComposer({ isExecuting: true });

  assert.equal(sendButton().hasAttribute("disabled"), true);
});

test("the queue slot renders above the input box", () => {
  renderComposer({
    children: [React.createElement(UserInput.Queue, { key: "queue", children: "queued message" })],
  });

  const queued = screen.getByText("queued message");
  assert.equal(
    queued.compareDocumentPosition(composer()) & window.Node.DOCUMENT_POSITION_FOLLOWING,
    window.Node.DOCUMENT_POSITION_FOLLOWING,
  );
});

test("insertText keeps the draft and inserts at the caret", async () => {
  const { ref } = renderComposer();

  type("read  now");
  composer().setSelectionRange(5, 5);
  await act(async () => {
    ref.current?.insertText("@file.ts");
  });

  assert.equal(composer().value, "read @file.ts  now");
});

test("insertText appends when the caret sits at the end", async () => {
  const { ref } = renderComposer();

  type("read");
  composer().setSelectionRange(4, 4);
  await act(async () => {
    ref.current?.insertText("@file.ts");
  });

  assert.equal(composer().value, "read@file.ts ");
});

test("setInput replaces the draft and starts collapsed", async () => {
  const { ref } = renderComposer();

  type("old draft");
  fireEvent.click(screen.getByRole("button", { name: "Expand input" }));
  await act(async () => {
    ref.current?.setInput("new draft");
  });

  assert.equal(composer().value, "new draft");
  assert.equal(ref.current?.value, "new draft");
  assert.ok(screen.getByRole("button", { name: "Expand input" }));
  fireEvent.click(screen.getByRole("button", { name: "Expand input" }));
  assert.ok(screen.getByRole("button", { name: "Collapse input" }));
});

test("user edits and the clear after sending report the value while setInput stays silent", async () => {
  const values: string[] = [];
  const { ref } = renderComposer({ values });

  await act(async () => {
    ref.current?.setInput("restored draft");
  });
  assert.deepEqual(values, []);

  type("read");
  composer().setSelectionRange(4, 4);
  await act(async () => {
    ref.current?.insertText("@file.ts");
  });
  fireEvent.click(sendButton());

  assert.deepEqual(values, ["read", "read@file.ts ", ""]);
});

test("the composer hosts context, toolbar, and help slots", () => {
  renderComposer({
    children: [
      React.createElement(UserInput.Context, { key: "context", children: "attached image" }),
      React.createElement(UserInput.BottomLeft, { key: "bottom", children: "toolbar" }),
      React.createElement(UserInput.Help, { key: "help", children: "custom help" }),
    ],
  });

  assert.ok(screen.getByText("attached image"));
  assert.ok(screen.getByText("toolbar"));
  assert.ok(screen.getByText("custom help"));
});

type SplitOptions = {
  executed?: Array<{ value: string; mode: string }>;
  preferred?: Array<string>;
  blocked?: string[];
  stops?: number[];
  isExecuting?: boolean;
  preferredSubmitMode?: "queue" | "steer";
  steerAvailability?: { supported: true } | { supported: false; reason: string };
};

function renderSplitComposer(options: SplitOptions = {}) {
  return render(
    React.createElement(UserInput, {
      expandOverlay: true,
      isExecuting: options.isExecuting ?? true,
      steerAvailability: options.steerAvailability ?? { supported: true },
      preferredSubmitMode: options.preferredSubmitMode ?? "queue",
      onExecute: (value: string, mode: string) => {
        options.executed?.push({ value, mode });
        return true;
      },
      onStop: () => options.stops?.push(1),
      onPreferredSubmitModeChange: (mode: string) => options.preferred?.push(mode),
      onSubmitBlocked: (reason: string) => options.blocked?.push(reason),
    } as never),
  );
}

async function openSendMenu() {
  await act(async () => {
    fireEvent.pointerDown(screen.getByRole("button", { name: "More send options" }), {
      button: 0,
      ctrlKey: false,
      pointerType: "mouse",
    });
  });
  return screen.findByRole("menu");
}

test("a running turn with content shows a split button that queues by default", () => {
  const executed: Array<{ value: string; mode: string }> = [];
  renderSplitComposer({ executed });
  type("next step");

  assert.equal(screen.queryByRole("button", { name: "Send message" }), null);
  assert.equal(screen.queryByRole("button", { name: "Steer running task" }), null);
  assert.ok(screen.getByRole("button", { name: "More send options" }));
  fireEvent.click(screen.getByRole("button", { name: "Queue message" }));

  assert.deepEqual(executed, [{ value: "next step", mode: "queue" }]);
  assert.equal(composer().value, "");
});

test("a Steer default makes the primary button steer", () => {
  const executed: Array<{ value: string; mode: string }> = [];
  renderSplitComposer({ executed, preferredSubmitMode: "steer" });
  type("change course");

  assert.equal(screen.queryByRole("button", { name: "Queue message" }), null);
  fireEvent.click(screen.getByRole("button", { name: "Steer running task" }));

  assert.deepEqual(executed, [{ value: "change course", mode: "steer" }]);
});

test("the arrow menu sends with the other mode and returns focus to the input", async () => {
  const executed: Array<{ value: string; mode: string }> = [];
  renderSplitComposer({ executed });
  type("change course");

  await openSendMenu();
  await act(async () => {
    fireEvent.click(screen.getByRole("menuitem", { name: /Steer this message instead/u }));
  });

  assert.deepEqual(executed, [{ value: "change course", mode: "steer" }]);
  // Radix hands focus back in a timer after the menu unmounts. Radix 在菜单卸载后用定时器交还焦点。
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 20));
  });
  assert.equal(window.document.activeElement === composer(), true);
});

test("with a Steer default the menu offers Queue", async () => {
  const executed: Array<{ value: string; mode: string }> = [];
  renderSplitComposer({ executed, preferredSubmitMode: "steer" });
  type("after this");

  await openSendMenu();
  await act(async () => {
    fireEvent.click(screen.getByRole("menuitem", { name: /Queue this message instead/u }));
  });

  assert.deepEqual(executed, [{ value: "after this", mode: "queue" }]);
});

test("the menu lists Steer and Queue with their descriptions and checks the default", async () => {
  renderSplitComposer({ preferredSubmitMode: "steer" });
  type("draft");

  await openSendMenu();
  const items = screen.getAllByRole("menuitemradio");

  assert.equal(items.length, 2);
  assert.match(items[0]?.textContent ?? "", /Steer\s*Process this message now/u);
  assert.match(items[1]?.textContent ?? "", /Queue\s*Run after current work finishes/u);
  assert.equal(items[0]?.getAttribute("aria-checked"), "true");
  assert.equal(items[1]?.getAttribute("aria-checked"), "false");
});

test("choosing a mode in the menu switches the default without sending", async () => {
  const preferred: string[] = [];
  const executed: Array<{ value: string; mode: string }> = [];
  renderSplitComposer({ preferred, executed });
  type("draft");

  await openSendMenu();
  await act(async () => {
    fireEvent.click(screen.getByRole("menuitemradio", { name: /Process this message now/u }));
  });

  assert.deepEqual(preferred, ["steer"]);
  assert.deepEqual(executed, []);
  assert.equal(composer().value, "draft");
});

test("choosing the current default in the menu changes nothing", async () => {
  const preferred: string[] = [];
  renderSplitComposer({ preferred });
  type("draft");

  await openSendMenu();
  await act(async () => {
    fireEvent.click(
      screen.getByRole("menuitemradio", { name: /Run after current work finishes/u }),
    );
  });

  assert.deepEqual(preferred, []);
});

test("Enter and Ctrl+Enter use the default mode and Alt+Enter uses the other mode", () => {
  const executed: Array<{ value: string; mode: string }> = [];
  renderSplitComposer({ executed });

  type("one");
  fireEvent.keyDown(composer(), { key: "Enter" });
  type("two");
  fireEvent.keyDown(composer(), { key: "Enter", ctrlKey: true });
  type("three");
  assert.equal(fireEvent.keyDown(composer(), { key: "Enter", altKey: true }), false);

  assert.deepEqual(executed, [
    { value: "one", mode: "queue" },
    { value: "two", mode: "queue" },
    { value: "three", mode: "steer" },
  ]);
});

test("Alt+Enter does nothing in the expanded composer where it inserts a newline", () => {
  const executed: Array<{ value: string; mode: string }> = [];
  renderSplitComposer({ executed });
  fireEvent.click(screen.getByRole("button", { name: "Expand input" }));
  type("draft");

  fireEvent.keyDown(composer(), { key: "Enter", altKey: true });

  assert.deepEqual(executed, []);
});

test("an unsupported target greys out Steer in the menu with its reason", async () => {
  const executed: Array<{ value: string; mode: string }> = [];
  renderSplitComposer({
    executed,
    steerAvailability: { supported: false, reason: "Claude Code cannot be steered." },
  });
  type("try steering");

  await openSendMenu();
  const row = screen.getByRole("menuitemradio", { name: /Steer/u });
  const footer = screen.getByRole("menuitem", { name: /Steer this message instead/u });

  assert.equal(row.getAttribute("aria-disabled"), "true");
  assert.match(row.textContent ?? "", /Claude Code cannot be steered\./u);
  assert.equal(footer.getAttribute("aria-disabled"), "true");
  fireEvent.click(row);
  fireEvent.click(footer);
  assert.deepEqual(executed, []);
});

test("Alt+Enter on an unsupported target only shows the reason", () => {
  const executed: Array<{ value: string; mode: string }> = [];
  const blocked: string[] = [];
  renderSplitComposer({
    executed,
    blocked,
    steerAvailability: { supported: false, reason: "Claude Code cannot be steered." },
  });
  type("try steering");

  fireEvent.keyDown(composer(), { key: "Enter", altKey: true });

  assert.deepEqual(executed, []);
  assert.deepEqual(blocked, ["Claude Code cannot be steered."]);
  assert.equal(composer().value, "try steering");
});

test("a Steer default on an unsupported target queues and explains why", () => {
  const executed: Array<{ value: string; mode: string }> = [];
  renderSplitComposer({
    executed,
    preferredSubmitMode: "steer",
    steerAvailability: { supported: false, reason: "Claude Code cannot be steered." },
  });
  type("still goes through");

  assert.match(screen.getByRole("status").textContent ?? "", /Claude Code cannot be steered\./u);
  fireEvent.click(screen.getByRole("button", { name: "Queue message" }));

  assert.deepEqual(executed, [{ value: "still goes through", mode: "queue" }]);
});

test("a running turn with an empty input stays the stop button and shows each mode's placeholder", () => {
  const stops: number[] = [];
  const { rerender } = renderSplitComposer({ stops });

  const queuePlaceholder = composer().placeholder;
  assert.equal(
    screen.getByRole("button", { name: "Stop execution" }).hasAttribute("disabled"),
    false,
  );
  assert.equal(screen.queryByRole("button", { name: "More send options" }), null);
  fireEvent.click(screen.getByRole("button", { name: "Stop execution" }));
  assert.deepEqual(stops, [1]);

  rerender(
    React.createElement(UserInput, {
      expandOverlay: true,
      isExecuting: true,
      steerAvailability: { supported: true },
      preferredSubmitMode: "steer",
      onStop: () => undefined,
    } as never),
  );
  assert.notEqual(composer().placeholder, queuePlaceholder);
});

test("the hint row names both shortcuts while a turn runs and disappears when idle", () => {
  const { rerender } = renderSplitComposer();

  assert.ok(screen.getByText("Enter to queue, Alt+Enter to steer"));

  rerender(
    React.createElement(UserInput, {
      expandOverlay: true,
      isExecuting: false,
      steerAvailability: { supported: true },
    } as never),
  );
  assert.equal(screen.queryByText(/Alt\+Enter/u), null);
});

test("an idle composer keeps the plain send even with Steer information", () => {
  const executed: Array<{ value: string; mode: string }> = [];
  renderSplitComposer({ executed, isExecuting: false });
  type("first message");

  assert.equal(screen.queryByRole("button", { name: "More send options" }), null);
  fireEvent.click(screen.getByRole("button", { name: "Send message" }));

  assert.deepEqual(executed, [{ value: "first message", mode: "queue" }]);
});
