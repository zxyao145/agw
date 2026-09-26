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

test("Ctrl+Enter and Shift+Enter send while a plain Enter keeps editing", () => {
  const executed: string[] = [];
  renderComposer({ executed });

  type("first");
  fireEvent.keyDown(composer(), { key: "Enter" });
  assert.deepEqual(executed, []);

  fireEvent.keyDown(composer(), { key: "Enter", ctrlKey: true });
  type("second");
  fireEvent.keyDown(composer(), { key: "Enter", shiftKey: true });

  assert.deepEqual(executed, ["first", "second"]);
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

test("a rejected submission keeps the draft", () => {
  const executed: string[] = [];
  const values: string[] = [];
  renderComposer({ executed, values, accept: false });

  type("keep me");
  fireEvent.click(sendButton());
  fireEvent.keyDown(composer(), { key: "Enter", shiftKey: true });

  assert.deepEqual(executed, ["keep me", "keep me"]);
  assert.equal(composer().value, "keep me");
  assert.deepEqual(values, ["keep me"]);
});

test("an accepted submission clears the draft and keeps focus on the input", () => {
  renderComposer();

  type("queued");
  fireEvent.click(sendButton());

  assert.equal(composer().value, "");
  assert.equal(window.document.activeElement, composer());
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

test("setInput replaces the whole draft", async () => {
  const { ref } = renderComposer();

  type("old draft");
  await act(async () => {
    ref.current?.setInput("new draft");
  });

  assert.equal(composer().value, "new draft");
  assert.equal(ref.current?.value, "new draft");
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
  assert.equal(screen.queryByText(/Ctrl\/Shift\+Enter to send/), null);
});

test("without a help slot the composer explains its send shortcut", () => {
  renderComposer();

  assert.ok(screen.getByText("Press Enter for new line • Ctrl/Shift+Enter to send"));
});
