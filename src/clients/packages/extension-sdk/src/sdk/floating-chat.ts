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
  button.style.cursor = "grab";
  button.style.touchAction = "none";
  button.style.userSelect = "none";
  const view = container.ownerDocument.defaultView;
  if (!view) throw new Error("Floating Chat buttons require a browsing context.");

  type ActivePointer = {
    id: number;
    originX: number;
    originY: number;
    originLeft: number;
    originTop: number;
    moved: boolean;
  };

  const dockInset = 24;
  const dragThreshold = 5;
  let dockSide: "left" | "right" = "right";
  let activePointer: ActivePointer | null = null;
  let suppressClick = false;
  let disposed = false;
  let hasDragged = false;
  let buttonWidth = 0;
  let buttonHeight = 0;
  let currentLeft = 0;
  let currentTop = 0;

  const clamp = (value: number, max: number) => Math.min(Math.max(value, 0), max);
  const rememberSize = (rect: DOMRect) => {
    if (rect.width === 0 || rect.height === 0) return;
    buttonWidth = rect.width;
    buttonHeight = rect.height;
  };
  const maxLeft = () => Math.max(0, view.innerWidth - buttonWidth);
  const maxTop = () => Math.max(0, view.innerHeight - buttonHeight);
  const setPosition = (left: number, top: number) => {
    currentLeft = clamp(left, maxLeft());
    currentTop = clamp(top, maxTop());
    button.style.left = `${currentLeft}px`;
    button.style.right = "auto";
    button.style.top = `${currentTop}px`;
    button.style.bottom = "auto";
  };
  const dockToEdge = (side: "left" | "right", top: number) => {
    const edge = maxLeft();
    const inset = Math.min(dockInset, edge / 2);
    currentLeft = side === "left" ? inset : edge - inset;
    currentTop = clamp(top, maxTop());
    button.style.left = side === "left" ? `${inset}px` : "auto";
    button.style.right = side === "right" ? `${inset}px` : "auto";
    button.style.top = `${currentTop}px`;
    button.style.bottom = "auto";
  };
  const dockAtInitialPosition = () => {
    const horizontalInset = Math.min(dockInset, maxLeft() / 2);
    const verticalInset = Math.min(dockInset, maxTop() / 2);
    button.style.left = "auto";
    button.style.right = `${horizontalInset}px`;
    button.style.top = "auto";
    button.style.bottom = `${verticalInset}px`;
  };
  const endPointer = (pointerId: number, suppressNextClick: boolean) => {
    const pointer = activePointer;
    if (!pointer || pointer.id !== pointerId) return;
    activePointer = null;
    button.style.cursor = "grab";
    if (!pointer.moved) return;

    const rect = button.getBoundingClientRect();
    rememberSize(rect);
    const distanceToLeft = currentLeft;
    const distanceToRight = view.innerWidth - currentLeft - buttonWidth;
    dockSide = distanceToLeft <= distanceToRight ? "left" : "right";
    dockToEdge(dockSide, currentTop);
    suppressClick = suppressNextClick;
  };
  const onPointerDown = (event: PointerEvent) => {
    if (disposed || !event.isPrimary || event.button !== 0 || activePointer) return;
    suppressClick = false;
    const rect = button.getBoundingClientRect();
    rememberSize(rect);
    currentLeft = rect.left;
    currentTop = rect.top;
    activePointer = {
      id: event.pointerId,
      originX: event.clientX,
      originY: event.clientY,
      originLeft: rect.left,
      originTop: rect.top,
      moved: false,
    };
    button.setPointerCapture(event.pointerId);
  };
  const onPointerMove = (event: PointerEvent) => {
    const pointer = activePointer;
    if (!pointer || pointer.id !== event.pointerId) return;
    const deltaX = event.clientX - pointer.originX;
    const deltaY = event.clientY - pointer.originY;
    if (!pointer.moved && deltaX * deltaX + deltaY * deltaY <= dragThreshold * dragThreshold)
      return;
    pointer.moved = true;
    hasDragged = true;
    button.style.cursor = "grabbing";
    setPosition(
      clamp(pointer.originLeft + deltaX, maxLeft()),
      clamp(pointer.originTop + deltaY, maxTop()),
    );
    event.preventDefault();
  };
  const onPointerUp = (event: PointerEvent) => endPointer(event.pointerId, true);
  const onPointerCancel = (event: PointerEvent) => {
    endPointer(event.pointerId, true);
    if (button.hasPointerCapture(event.pointerId)) button.releasePointerCapture(event.pointerId);
  };
  const onLostPointerCapture = (event: PointerEvent) => endPointer(event.pointerId, true);
  const onResize = () => {
    const rect = button.getBoundingClientRect();
    rememberSize(rect);
    if (activePointer?.moved) {
      setPosition(
        rect.width > 0 ? rect.left : currentLeft,
        rect.height > 0 ? rect.top : currentTop,
      );
      return;
    }
    if (hasDragged) {
      dockToEdge(dockSide, rect.height > 0 ? rect.top : currentTop);
      return;
    }
    dockAtInitialPosition();
  };
  const onButtonResize = () => {
    const rect = button.getBoundingClientRect();
    if (rect.width === 0 || rect.height === 0) return;
    rememberSize(rect);
    if (activePointer?.moved) {
      setPosition(rect.left, rect.top);
      return;
    }
    if (hasDragged) {
      dockToEdge(dockSide, rect.top);
      return;
    }
    dockAtInitialPosition();
  };
  const onClick = (event: MouseEvent) => {
    if (suppressClick) {
      suppressClick = false;
      if (event.detail > 0) {
        event.preventDefault();
        return;
      }
    }
    options.onOpen();
  };

  button.addEventListener("pointerdown", onPointerDown);
  button.addEventListener("pointermove", onPointerMove);
  button.addEventListener("pointerup", onPointerUp);
  button.addEventListener("pointercancel", onPointerCancel);
  button.addEventListener("lostpointercapture", onLostPointerCapture);
  button.addEventListener("click", onClick);
  view.addEventListener("resize", onResize);
  container.append(button);
  rememberSize(button.getBoundingClientRect());
  dockAtInitialPosition();
  const resizeObserver = new view.ResizeObserver(onButtonResize);
  resizeObserver.observe(button);
  return {
    focus() {
      button.focus();
    },
    dispose() {
      if (disposed) return;
      disposed = true;
      if (activePointer && button.hasPointerCapture(activePointer.id)) {
        const pointerId = activePointer.id;
        activePointer = null;
        button.releasePointerCapture(pointerId);
      }
      activePointer = null;
      button.removeEventListener("pointerdown", onPointerDown);
      button.removeEventListener("pointermove", onPointerMove);
      button.removeEventListener("pointerup", onPointerUp);
      button.removeEventListener("pointercancel", onPointerCancel);
      button.removeEventListener("lostpointercapture", onLostPointerCapture);
      button.removeEventListener("click", onClick);
      view.removeEventListener("resize", onResize);
      resizeObserver.disconnect();
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
