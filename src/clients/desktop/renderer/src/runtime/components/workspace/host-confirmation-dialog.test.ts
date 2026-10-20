import assert from "node:assert/strict";
import test, { afterEach, before } from "node:test";

import type { HostConfirmation } from "../../extensions/extension-host";

type DomEnvironment = Awaited<ReturnType<typeof import("@agw/test-harness").setupDomEnvironment>>;
type DialogModule = typeof import("./host-confirmation-dialog");
type MessageConfirmation = Extract<HostConfirmation, { kind: "message" }>;

// setupDomEnvironment 注册关闭 window 的 after 钩子；在 before 中调用时它在 before 结束后立即运行，Esc 的监听随之移除。
// 模块加载时调用，钩子属于整个文件。
// setupDomEnvironment registers an after hook that closes the window; called inside before, it runs as soon as before
// ends and the Escape listener goes with it. Called at module load, the hook belongs to the file.
const environmentReady = import("@agw/test-harness").then(({ setupDomEnvironment }) =>
  setupDomEnvironment(),
);

let environment: DomEnvironment;
let dialogs: DialogModule;

before(async () => {
  environment = await environmentReady;
  dialogs = await import("./host-confirmation-dialog");
});
afterEach(() => environment.cleanup());

const conflict: MessageConfirmation = {
  kind: "message",
  id: 1,
  extensionName: "Sample",
  type: "warning",
  message: "notes.md was changed on disk after it was opened.",
  detail: "Overwrite it with your version, or reload it and discard your changes.",
  buttons: ["Overwrite", "Reload", "Cancel"],
  defaultId: 1,
  cancelId: 2,
};

/** 对话框前面放一个页面中的按钮，用来检查关闭后焦点回到哪里。A page button before the dialog shows where focus returns. */
function renderDialog(confirmation: HostConfirmation | null) {
  const { React } = environment;
  const answers: (boolean | number)[] = [];
  const element = (value: HostConfirmation | null) =>
    React.createElement(
      React.Fragment,
      null,
      React.createElement("button", { type: "button" }, "Outside"),
      React.createElement(dialogs.HostConfirmationDialogView, {
        confirmation: value,
        onAnswer: (answer: boolean | number) => answers.push(answer),
      }),
    ) as Parameters<DomEnvironment["render"]>[0];
  const result = environment.render(element(confirmation));
  return { answers, rerender: (next: HostConfirmation | null) => result.rerender(element(next)) };
}

function footerButtons(dialog: HTMLElement): HTMLElement[] {
  const footer = dialog.querySelector<HTMLElement>('[data-slot="dialog-footer"]');
  assert.ok(footer);
  return environment.within(footer).getAllByRole("button");
}

/**
 * 断言焦点所在的元素。失败消息中只放文字：node:assert 展开 jsdom 元素会让测试长时间卡住。
 * Asserts the focused element. Failure messages carry text only: node:assert expanding a jsdom element hangs the test.
 */
function assertFocused(element: Element): void {
  const focused = document.activeElement;
  assert.ok(
    focused === element,
    `Expected focus on "${element.textContent}", found <${focused?.tagName.toLowerCase()}> "${focused?.textContent?.slice(0, 40)}".`,
  );
}

test("a message dialog is titled with the extension name, keeps the button order and focuses defaultId", () => {
  const { answers } = renderDialog(conflict);
  const dialog = environment.screen.getByRole("dialog", { name: "Sample" });
  const text = dialog.textContent ?? "";
  assert.ok(text.indexOf("Sample") < text.indexOf(conflict.message));
  assert.ok(text.indexOf(conflict.message) < text.indexOf(conflict.detail!));

  const buttons = footerButtons(dialog);
  assert.deepEqual(
    buttons.map((button) => button.textContent),
    ["Overwrite", "Reload", "Cancel"],
  );
  assertFocused(buttons[1]);
  environment.fireEvent.click(buttons[0]);
  assert.deepEqual(answers, [0]);
});

test("Escape and the close button answer cancelId, and the next message focuses its own defaultId", () => {
  const { answers, rerender } = renderDialog(conflict);
  environment.fireEvent.keyDown(document.activeElement!, { key: "Escape" });
  assert.deepEqual(answers, [2]);
  environment.fireEvent.click(environment.screen.getByRole("button", { name: "Close" }));
  assert.deepEqual(answers, [2, 2]);

  rerender({
    ...conflict,
    id: 2,
    type: null,
    message: "Discard your changes to notes.md?",
    detail: null,
    buttons: ["Discard", "Keep"],
    defaultId: 0,
    cancelId: 1,
  });
  const dialog = environment.screen.getByRole("dialog", { name: "Sample" });
  assert.equal(dialog.textContent?.includes(conflict.detail!), false);
  assertFocused(footerButtons(dialog)[0]);
  environment.fireEvent.keyDown(document.activeElement!, { key: "Escape" });
  assert.deepEqual(answers, [2, 2, 1]);
});

test("a following message box without defaultId focuses its first button, and closing returns focus to the earlier element", async () => {
  const { rerender } = renderDialog(null);
  const outside = environment.screen.getByRole("button", { name: "Outside" });
  outside.focus();

  rerender(conflict);
  assertFocused(footerButtons(environment.screen.getByRole("dialog", { name: "Sample" }))[1]);
  rerender({
    ...conflict,
    id: 2,
    message: "Discard your changes to notes.md?",
    buttons: ["Discard", "Keep"],
    defaultId: null,
    cancelId: 1,
  });
  const second = footerButtons(environment.screen.getByRole("dialog", { name: "Sample" }));
  assert.deepEqual(
    second.map((button) => button.textContent),
    ["Discard", "Keep"],
  );
  assertFocused(second[0]);

  rerender(null);
  await environment.waitFor(() => assertFocused(outside));
});

test("a message box that opens while the previous dialog is still closing has its overlay beneath its content", async () => {
  // jsdom 的计算样式不随属性变化，也不播放动画；这里让计算样式实时读取，并手动结束遮罩的退出动画，得到浏览器中遮罩
  // （150 ms）先于内容（200 ms）结束退出动画的那段时间。
  // jsdom's computed styles do not follow attribute changes and it plays no animations; here computed styles are read
  // live and the overlay's exit animation is ended by hand, giving the window in a browser where the overlay (150 ms)
  // has finished its exit animation and the content (200 ms) has not.
  const style = document.createElement("style");
  style.textContent = `
    [data-slot="dialog-overlay"][data-state="open"], [data-slot="dialog-content"][data-state="open"] { animation-name: enter; }
    [data-slot="dialog-overlay"][data-state="closed"], [data-slot="dialog-content"][data-state="closed"] { animation-name: exit; }`;
  document.head.append(style);
  const globals = globalThis as unknown as Record<string, unknown>;
  const original = { getComputedStyle: globals.getComputedStyle, CSS: globals.CSS };
  Object.defineProperty(globalThis, "getComputedStyle", {
    configurable: true,
    value: (element: Element) =>
      new Proxy(
        {},
        { get: (_target, name) => Reflect.get(window.getComputedStyle(element), name) },
      ),
  });
  Object.defineProperty(globalThis, "CSS", { configurable: true, value: window.CSS });
  const finishExit = (element: Element) => {
    const finished = new window.Event("animationend");
    Object.defineProperty(finished, "animationName", { value: "exit" });
    environment.fireEvent(element, finished);
  };
  try {
    const { rerender } = renderDialog(null);
    const outside = environment.screen.getByRole("button", { name: "Outside" });
    outside.focus();
    rerender(conflict);
    rerender(null);
    const overlay = document.querySelector('[data-slot="dialog-overlay"]');
    assert.ok(overlay);
    finishExit(overlay);
    assert.equal(document.querySelector('[data-slot="dialog-overlay"]'), null);
    assert.equal(
      document.querySelector('[data-slot="dialog-content"]')?.getAttribute("data-state"),
      "closed",
    );

    rerender({
      ...conflict,
      id: 2,
      message: "notes.md was changed on disk again.",
      buttons: ["Overwrite", "Reload", "Cancel"],
    });
    const overlays = document.querySelectorAll('[data-slot="dialog-overlay"]');
    const contents = document.querySelectorAll<HTMLElement>('[data-slot="dialog-content"]');
    assert.equal(overlays.length, 1);
    assert.equal(contents.length, 1);
    assert.ok(
      overlays[0].compareDocumentPosition(contents[0]) & Node.DOCUMENT_POSITION_FOLLOWING,
      "the overlay comes before the content, so the content is drawn above it",
    );
    assert.ok(contents[0].textContent?.includes("changed on disk again"));
    assertFocused(footerButtons(contents[0])[1]);

    // 第二个对话框关闭后，焦点回到第一个对话框打开前的元素。After the second dialog closes, focus returns to the element
    // focused before the first dialog opened.
    rerender(null);
    finishExit(document.querySelector('[data-slot="dialog-overlay"]')!);
    finishExit(document.querySelector('[data-slot="dialog-content"]')!);
    assert.equal(document.querySelector('[data-slot="dialog-content"]'), null);
    await environment.waitFor(() => assertFocused(outside));
  } finally {
    Object.defineProperty(globalThis, "getComputedStyle", {
      configurable: true,
      value: original.getComputedStyle,
    });
    Object.defineProperty(globalThis, "CSS", { configurable: true, value: original.CSS });
    style.remove();
  }
});

test("every allowed set of labels and texts can wrap inside the message dialog", () => {
  const labels = ["a", "b", "c", "d"].map((letter) => letter.repeat(40));
  renderDialog({
    ...conflict,
    message: "m".repeat(500),
    detail: "d".repeat(2000),
    buttons: labels,
    defaultId: 0,
    cancelId: 3,
  });
  const dialog = environment.screen.getByRole("dialog", { name: "Sample" });
  // jsdom 不计算布局，这里检查换行所依赖的 class。jsdom does not lay out, so this checks the classes wrapping relies on.
  const footer = dialog.querySelector<HTMLElement>('[data-slot="dialog-footer"]')!;
  assert.ok(footer.classList.contains("flex-wrap"));
  const buttons = footerButtons(dialog);
  assert.deepEqual(
    buttons.map((button) => button.textContent),
    labels,
  );
  for (const button of buttons) {
    for (const name of [
      "whitespace-normal",
      "wrap-anywhere",
      "min-w-0",
      "max-w-full",
      "shrink",
      "h-auto",
    ]) {
      assert.ok(button.classList.contains(name), `${name} on ${button.textContent}`);
    }
    for (const name of ["whitespace-nowrap", "shrink-0", "h-9"]) {
      assert.equal(button.classList.contains(name), false, `${name} on ${button.textContent}`);
    }
  }
  // 没有空格的长文字同样可以换行。Long text without spaces wraps too.
  for (const text of ["m".repeat(500), "d".repeat(2000)]) {
    assert.ok(environment.screen.getByText(text).classList.contains("wrap-anywhere"));
  }
});

test("discard and open-link confirmations still answer true or false", () => {
  const discard = renderDialog({
    kind: "discard-unsaved",
    items: [{ fileName: "notes.md", viewerTitle: "Viewer", extensionName: "Sample" }],
  });
  environment.fireEvent.click(environment.screen.getByRole("button", { name: "Cancel" }));
  environment.fireEvent.click(environment.screen.getByRole("button", { name: "Discard" }));
  environment.fireEvent.keyDown(document.activeElement!, { key: "Escape" });
  assert.deepEqual(discard.answers, [false, true, false]);
  environment.cleanup();

  const link = renderDialog({
    kind: "open-link",
    extensionName: "Sample",
    url: "https://example.com/docs",
  });
  environment.fireEvent.click(environment.screen.getByRole("button", { name: "Open" }));
  assert.deepEqual(link.answers, [true]);
});
