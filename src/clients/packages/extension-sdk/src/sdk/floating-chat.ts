import type { FloatingChatHandle, FloatingChatOptions } from "../api";
import { createExtensionError } from "../rpc";

export type FloatingChatButton = { focus(): void; dispose(): void };

export function normalizeFloatingChatContext(value: unknown): Record<string, string> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw createExtensionError("ERR_INVALID_ARGUMENT", "Chat context must be an object.");
  }
  const entries = Object.entries(value);
  for (const [key, entry] of entries) {
    if (typeof entry !== "string") {
      throw createExtensionError(
        "ERR_INVALID_ARGUMENT",
        `Chat context value "${key}" must be a string.`,
      );
    }
  }
  return Object.fromEntries(entries) as Record<string, string>;
}

/** 在指定 DOM 容器中创建可复用的 Chat 按钮。Creates the reusable Chat button in a DOM container. */
export function mountFloatingChatButton(
  container: HTMLElement,
  options: { onOpen(): void },
): FloatingChatButton {
  const button = container.ownerDocument.createElement("button");
  button.type = "button";
  button.textContent = "Chat";
  button.setAttribute("aria-label", "Open chat");
  button.title = "Open chat";
  button.style.position = "fixed";
  button.style.right = "24px";
  button.style.bottom = "24px";
  button.style.zIndex = "2147483000";
  button.style.borderRadius = "9999px";
  button.style.padding = "12px 18px";
  button.style.background = "var(--primary, #2563eb)";
  button.style.color = "var(--primary-foreground, #ffffff)";
  button.style.boxShadow = "0 4px 16px rgb(0 0 0 / 20%)";
  button.addEventListener("click", options.onOpen);
  container.append(button);
  return {
    focus() {
      button.focus();
    },
    dispose() {
      button.removeEventListener("click", options.onOpen);
      button.remove();
    },
  };
}

export async function createFloatingChat(
  call: <T>(method: string, params?: unknown) => Promise<T>,
  options: FloatingChatOptions = {},
  onError: (error: unknown) => void,
  onContextCleared: (listener: () => void) => { dispose(): void },
  onFocus: (listener: () => void) => { dispose(): void },
): Promise<FloatingChatHandle> {
  let context = normalizeFloatingChatContext(options.context ?? {});
  let disposed = false;
  let button: FloatingChatButton | null = null;
  const send = (
    method:
      | "chat.floating.context"
      | "chat.floating.open"
      | "chat.floating.close"
      | "chat.floating.dispose",
  ) => call(method, { context });
  const assertActive = () => {
    if (disposed)
      throw createExtensionError("ERR_FRAME_GONE", "The floating Chat component was disposed.");
  };
  const contextSubscription = onContextCleared(() => {
    context = {};
  });
  const focusSubscription = onFocus(() => button?.focus());
  const handle: FloatingChatHandle = {
    async setContext(nextContext) {
      assertActive();
      context = normalizeFloatingChatContext(nextContext);
      await send("chat.floating.context");
    },
    async open() {
      assertActive();
      await send("chat.floating.open");
    },
    async close() {
      assertActive();
      await send("chat.floating.close");
    },
    async dispose() {
      if (disposed) return;
      disposed = true;
      button?.dispose();
      contextSubscription.dispose();
      focusSubscription.dispose();
      await send("chat.floating.dispose");
    },
  };
  try {
    await call("chat.floating.register", { context });
  } catch (error) {
    contextSubscription.dispose();
    focusSubscription.dispose();
    throw error;
  }
  button = mountFloatingChatButton(document.body, {
    onOpen: () => void handle.open().catch(onError),
  });
  return handle;
}
