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
