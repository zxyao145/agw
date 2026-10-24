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

/** 按文字点击确认窗口中的按钮，并等待窗口关闭。Clicks a confirmation button by its text and waits for the window to close. */
export async function answerConfirmation(label: string, timeoutMs = 20_000): Promise<string> {
  const window = await waitForConfirmationWindow(timeoutMs);
  const text = await confirmationText(window);
  const clicked = (await window.webContents.executeJavaScript(
    `(() => {
      const button = [...document.querySelectorAll("footer button")].find((item) => item.textContent === ${JSON.stringify(label)});
      if (!button) return false;
      button.click();
      return true;
    })()`,
    true,
  )) as boolean;
  if (!clicked) throw new Error(`The confirmation has no "${label}" button: ${text}`);
  const deadline = Date.now() + timeoutMs;
  while (!window.isDestroyed() && Date.now() < deadline) await delay(50);
  if (!window.isDestroyed()) throw new Error("The confirmation window did not close.");
  return text;
}
