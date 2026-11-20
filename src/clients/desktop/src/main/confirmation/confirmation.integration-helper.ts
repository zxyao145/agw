// 集成测试用：在真实的确认窗口中读取内容并点击按钮。For integration tests: reads and clicks in real confirmation windows.
import { setTimeout as delay } from "node:timers/promises";
import { BrowserWindow } from "electron";

import { CONFIRMATION_PAGE_URL } from "./confirmation-protocol";

/** 当前显示的确认窗口。The confirmation window currently shown. */
export function visibleConfirmationWindow(): BrowserWindow | null {
  return (
    BrowserWindow.getAllWindows().find(
      (window) =>
        !window.isDestroyed() &&
        window.isVisible() &&
        window.webContents.getURL() === CONFIRMATION_PAGE_URL,
    ) ?? null
  );
}

/** 等待确认窗口显示。Waits for a confirmation window to show. */
export async function waitForConfirmationWindow(timeoutMs = 20_000): Promise<BrowserWindow> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const window = visibleConfirmationWindow();
    if (window) return window;
    await delay(50);
  }
  throw new Error("Timed out waiting for a confirmation window.");
}

/** 确认窗口的文字。The confirmation window's text. */
export async function confirmationText(window: BrowserWindow): Promise<string> {
  return (await window.webContents.executeJavaScript(
    "document.getElementById('confirmation').innerText",
    true,
  )) as string;
}

/** 用原生鼠标事件点击确认按钮，并等待窗口关闭。Clicks a confirmation button with native mouse events and waits for the window to close. */
export async function answerConfirmation(label: string, timeoutMs = 20_000): Promise<string> {
  const window = await waitForConfirmationWindow(timeoutMs);
  const parent = window.getParentWindow();
  const text = await confirmationText(window);
  const point = (await window.webContents.executeJavaScript(
    `(() => {
      const button = [...document.querySelectorAll("footer button")].find((item) => item.textContent === ${JSON.stringify(label)});
      if (!button) return null;
      const rect = button.getBoundingClientRect();
      const x = rect.left + rect.width / 2;
      const y = rect.top + rect.height / 2;
      const hit = document.elementFromPoint(x, y);
      return rect.width > 0 && rect.height > 0 && (hit === button || button.contains(hit)) ? { x, y } : null;
    })()`,
    true,
  )) as { x: number; y: number } | null;
  if (!point) throw new Error(`The confirmation has no clickable "${label}" button: ${text}`);
  window.focus();
  const contents = window.webContents;
  contents.focus();
  const x = Math.round(point.x);
  const y = Math.round(point.y);
  contents.sendInputEvent({ type: "mouseMove", x, y });
  contents.sendInputEvent({ type: "mouseDown", x, y, button: "left", clickCount: 1 });
  contents.sendInputEvent({ type: "mouseUp", x, y, button: "left", clickCount: 1 });
  const deadline = Date.now() + timeoutMs;
  while (!window.isDestroyed() && Date.now() < deadline) await delay(50);
  if (!window.isDestroyed()) throw new Error("The confirmation window did not close.");
  if (parent && !parent.isDestroyed()) parent.focus();
  return text;
}
