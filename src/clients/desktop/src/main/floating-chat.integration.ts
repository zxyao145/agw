import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { once } from "node:events";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { createInterface } from "node:readline";
import { setTimeout as delay } from "node:timers/promises";
import { app, BrowserWindow, protocol, WebContentsView } from "electron";

import {
  FloatingChatButtonView,
  registerFloatingChatButtonIpc,
} from "./floating-chat/floating-chat-button";
import { FLOATING_CHAT_BUTTON_VIEW_SIZE } from "./floating-chat/floating-chat-geometry";
import { FLOATING_CHAT_SCHEME_PRIVILEGES } from "./floating-chat/floating-chat-protocol";

const directory = resolve(".scratch/floating-chat-component/user-data");
mkdirSync(directory, { recursive: true });
app.setPath("userData", directory);
protocol.registerSchemesAsPrivileged([FLOATING_CHAT_SCHEME_PRIVILEGES]);
app.on("window-all-closed", () => undefined);

async function until<T>(read: () => T | Promise<T>, label: string): Promise<NonNullable<T>> {
  const deadline = Date.now() + 5000;
  while (Date.now() < deadline) {
    const value = await read();
    if (value) return value as NonNullable<T>;
    await delay(16);
  }
  throw new Error(`Timed out waiting for ${label}.`);
}

async function run(): Promise<void> {
  const mouseExecutable = resolve(".scratch/floating-chat-component/native-mouse");
  const mouseProcess = spawn(mouseExecutable, [], { stdio: ["pipe", "pipe", "inherit"] });
  const mouseClosed = once(mouseProcess, "exit");
  const responses = createInterface({ input: mouseProcess.stdout })[Symbol.asyncIterator]();
  const nativeMouse = async (
    type: string,
    x: number,
    y: number,
  ): Promise<{ x: number; y: number }> => {
    mouseProcess.stdin.write(`${JSON.stringify({ type, x, y })}\n`);
    const response = await responses.next();
    if (response.done) throw new Error("The native mouse test driver terminated.");
    return JSON.parse(response.value) as { x: number; y: number };
  };
  const initialCursor = await nativeMouse("position", 0, 0);
  await app.whenReady();
  let window = new BrowserWindow({ width: 1000, height: 720, show: false });
  await window.loadURL("data:text/html,<body><aside style='width:280px'>Sidebar</aside></body>");
  const editor = new WebContentsView();
  await editor.webContents.loadURL("data:text/html,<textarea id='editor' autofocus></textarea>");
  editor.setBounds({ x: 280, y: 60, width: 720, height: 600 });
  window.contentView.addChildView(editor);
  const opened: string[] = [];
  const button = new FloatingChatButtonView({
    getMainWindow: () => window,
    preloadPath: resolve("dist/preload/floating-chat.js"),
    pageDirectory: resolve("dist/floating-chat"),
    onOpen: (owner) => opened.push(owner),
  });
  registerFloatingChatButtonIpc(button, {
    assertHost(event, hostInstanceId) {
      assert.equal(event.sender, window.webContents);
      assert.equal(hostInstanceId, "component-host");
    },
  });
  const request = {
    owner: "first",
    background: "#202020",
    foreground: "#ffffff",
    border: "#5f6060",
  };
  button.update(request);
  const widget = () =>
    window.contentView.children.find(
      (view): view is WebContentsView =>
        view instanceof WebContentsView && view.webContents === button.webContents,
    );
  await until(() => widget()?.getVisible(), "the native Chat button");
  window.show();
  app.focus({ steal: true });
  window.focus();
  const contents = () => {
    assert.ok(button.webContents);
    return button.webContents;
  };
  const center = async (): Promise<{ x: number; y: number }> =>
    contents().executeJavaScript(`(() => {
      const rect = document.querySelector('button').getBoundingClientRect();
      return {x: Math.round(rect.left + rect.width / 2), y: Math.round(rect.top + rect.height / 2)};
    })()`);
  const mouse = async (
    type: "mouseMove" | "mouseDown" | "mouseUp",
    point: { x: number; y: number },
    pressed = false,
  ) => {
    const parent = window.getContentBounds();
    const view = button.viewBounds!;
    const event =
      type === "mouseDown" ? "down" : type === "mouseUp" ? "up" : pressed ? "drag" : "move";
    await nativeMouse(event, parent.x + view.x + point.x, parent.y + view.y + point.y);
  };
  const focusButton = async () => {
    app.focus({ steal: true });
    window.focus();
    contents().focus();
    await until(
      async () => window.isFocused() && (await contents().executeJavaScript("document.hasFocus()")),
      "the native button window focus",
    );
  };
  const down = async () => {
    await contents().executeJavaScript(`(() => {
      window.pointerEvents = [];
      if (window.tracksPointerEvents) return;
      window.tracksPointerEvents = true;
      for (const type of ['pointerdown', 'pointermove', 'pointerup', 'lostpointercapture']) {
        document.addEventListener(type, () => window.pointerEvents.push(type), true);
      }
    })()`);
    const point = await center();
    await focusButton();
    await mouse("mouseMove", point);
    await mouse("mouseDown", point);
    await until(() => button.getState().dragging, "the native drag to start");
    const [width, height] = window.getContentSize();
    assert.deepEqual(button.viewBounds, { x: 0, y: 0, width, height });
  };
  const dragTo = async (x: number, y: number) => {
    await down();
    await mouse("mouseMove", { x, y }, true);
    await delay(32);
    await mouse("mouseUp", { x, y });
    await until(() => !button.getState().dragging, "the native drag to end");
    assert.equal(button.viewBounds?.width, FLOATING_CHAT_BUTTON_VIEW_SIZE);
    assert.equal(button.viewBounds?.height, FLOATING_CHAT_BUTTON_VIEW_SIZE);
    assert.deepEqual(opened, []);
    const events = (await contents().executeJavaScript("window.pointerEvents")) as string[];
    const start = events.indexOf("pointerdown");
    const move = events.indexOf("pointermove", start + 1);
    const end = events.indexOf("pointerup", move + 1);
    const lost = events.indexOf("lostpointercapture");
    assert.ok(start >= 0 && move > start && end > move, JSON.stringify(events));
    assert.ok(lost === -1 || lost > end, JSON.stringify(events));
  };
  try {
    assert.equal(window.contentView.children.at(-1), widget());
    assert.equal(
      await contents().executeJavaScript("document.querySelector('button').textContent"),
      "",
    );
    assert.equal(
      await contents().executeJavaScript(
        "document.querySelectorAll('button svg[aria-hidden=true]').length",
      ),
      1,
    );
    await dragTo(32, 260);
    assert.equal(button.viewBounds?.x, 0);
    assert.ok(editor.getVisible());
    await dragTo(window.getContentSize()[0] - 32, 400);
    assert.equal(button.viewBounds?.x, window.getContentSize()[0] - FLOATING_CHAT_BUTTON_VIEW_SIZE);
    console.log("PASS icon button drags across Sidebar and editor to both application edges");

    window.setContentSize(680, 460);
    await until(
      () => button.viewBounds?.x === 680 - FLOATING_CHAT_BUTTON_VIEW_SIZE,
      "the docked button after resize",
    );
    assert.ok(button.viewBounds && button.viewBounds.y + FLOATING_CHAT_BUTTON_VIEW_SIZE <= 460);
    editor.webContents.focus();
    await editor.webContents.executeJavaScript("document.querySelector('textarea').focus()");
    editor.webContents.sendInputEvent({ type: "char", keyCode: "k" });
    await until(
      async () =>
        (await editor.webContents.executeJavaScript("document.querySelector('textarea').value")) ===
        "k",
      "editing outside the native button",
    );
    console.log("PASS resize keeps the button visible and the editor remains interactive");

    const point = await center();
    await focusButton();
    await mouse("mouseMove", point);
    await mouse("mouseDown", point);
    await until(() => button.getState().dragging, "the click capture layer");
    const position = button.getState();
    await mouse("mouseUp", {
      x: position.x + FLOATING_CHAT_BUTTON_VIEW_SIZE / 2,
      y: position.y + FLOATING_CHAT_BUTTON_VIEW_SIZE / 2,
    });
    await until(() => opened.length === 1, "clicking the Chat icon");
    button.update({ ...request, owner: null });
    assert.equal(widget()?.getVisible(), false);
    button.update({ ...request, focus: true });
    await until(
      async () =>
        await contents().executeJavaScript(
          "document.activeElement?.getAttribute('aria-label') === 'Open chat'",
        ),
      "keyboard focus on the Chat icon",
    );
    contents().sendInputEvent({ type: "keyDown", keyCode: "Enter" });
    contents().sendInputEvent({ type: "char", keyCode: "\r" });
    contents().sendInputEvent({ type: "keyUp", keyCode: "Enter" });
    await until(() => opened.length === 2, "Enter on the Chat icon");
    assert.deepEqual(opened, ["first", "first"]);
    console.log("PASS icon click, hide, restored focus and Enter activate the current owner");

    await down();
    window.hide();
    await until(
      () => !button.getState().dragging,
      "a hidden parent to release the full-window drag layer",
    );
    await nativeMouse("up", initialCursor.x, initialCursor.y);
    window.show();
    assert.equal(button.viewBounds?.width, FLOATING_CHAT_BUTTON_VIEW_SIZE);
    console.log("PASS hiding the parent cancels the full-window drag layer");

    await down();
    window.destroy();
    await nativeMouse("up", initialCursor.x, initialCursor.y);
    assert.equal(button.getState().dragging, false);
    window = new BrowserWindow({ width: 900, height: 640, show: false });
    await window.loadURL("data:text/html,<body>New parent</body>");
    button.update({ ...request, owner: "second" });
    await until(() => widget()?.getVisible(), "the button in the new parent");
    assert.equal(button.viewBounds?.width, FLOATING_CHAT_BUTTON_VIEW_SIZE);
    console.log("PASS closing a dragging parent creates a compact button in the next window");
    button.reset();
    assert.equal(widget()?.getVisible(), false);
  } finally {
    await nativeMouse("up", initialCursor.x, initialCursor.y);
    await nativeMouse("move", initialCursor.x, initialCursor.y);
    mouseProcess.stdin.end();
    const [mouseExitCode] = await mouseClosed;
    assert.equal(mouseExitCode, 0);
    if (!editor.webContents.isDestroyed()) editor.webContents.close();
    if (!window.isDestroyed()) window.destroy();
  }
}

void run().then(
  () => app.exit(0),
  (error: unknown) => {
    console.error(error);
    app.exit(1);
  },
);
