import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";

import type { UserInputRef } from "./user-input";

const { React, act, fireEvent, render, screen, window } = await setupDomEnvironment();
const { UserInput } = await import("./user-input.tsx");

type ComposerOptions = {
  executed?: string[];
  stops?: number[];
  isExecuting?: boolean;
  isDisabled?: boolean;
  isSubmitDisabled?: boolean;
  hasAdditionalInput?: boolean;
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
        hasAdditionalInput: options.hasAdditionalInput,
        onExecute: (value: string) => options.executed?.push(value),
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

test("additional input alone can be submitted without text", () => {
  const executed: string[] = [];
  renderComposer({ executed, hasAdditionalInput: true });

  assert.equal(sendButton().hasAttribute("disabled"), false);
  fireEvent.click(sendButton());

  assert.deepEqual(executed, [""]);
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
  renderComposer({ isDisabled: true, hasAdditionalInput: true });

  assert.equal(composer().hasAttribute("disabled"), true);
  assert.equal(sendButton().hasAttribute("disabled"), true);
});

test("a running execution turns the action into a stop request", () => {
  const executed: string[] = [];
  const stops: number[] = [];
  renderComposer({ executed, stops, isExecuting: true, onStop: true });

  assert.equal(composer().hasAttribute("disabled"), true);
  fireEvent.click(sendButton());

  assert.deepEqual(stops, [1]);
  assert.deepEqual(executed, []);
});

test("a running execution without a stop handler offers no action", () => {
  renderComposer({ isExecuting: true });

  assert.equal(sendButton().hasAttribute("disabled"), true);
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
