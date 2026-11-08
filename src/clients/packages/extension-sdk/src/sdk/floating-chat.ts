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
  options: {
    onOpen(): void;
    onDrag?(event: {
      phase: "start" | "move" | "end" | "cancel";
      screenX: number;
      screenY: number;
    }): void;
  },
): FloatingChatButton {
  const button = container.ownerDocument.createElement("button");
  button.type = "button";
  const icon = container.ownerDocument.createElementNS("http://www.w3.org/2000/svg", "svg");
  icon.setAttribute("viewBox", "0 0 24 24");
  icon.setAttribute("width", "18");
  icon.setAttribute("height", "18");
  icon.setAttribute("fill", "none");
  icon.setAttribute("stroke", "currentColor");
  icon.setAttribute("stroke-width", "1.75");
  icon.setAttribute("stroke-linecap", "round");
  icon.setAttribute("stroke-linejoin", "round");
  icon.setAttribute("aria-hidden", "true");
  const path = container.ownerDocument.createElementNS("http://www.w3.org/2000/svg", "path");
  path.setAttribute(
    "d",
    "M2.992 16.342a2 2 0 0 1 .094 1.167l-1.065 3.29a1 1 0 0 0 1.236 1.168l3.413-.998a2 2 0 0 1 1.099.092 10 10 0 1 0-4.777-4.719",
  );
  icon.append(path);
  button.append(icon);
  button.setAttribute("aria-label", "Open chat");
  button.title = "Open chat";
  const windowManaged = options.onDrag !== undefined;
  button.style.position = windowManaged ? "relative" : "fixed";
  button.style.zIndex = "2147483000";
  button.style.borderRadius = "9999px";
  button.style.width = "36px";
  button.style.height = "36px";
  button.style.padding = "0";
  button.style.display = "grid";
  button.style.placeItems = "center";
  button.style.boxSizing = "border-box";
  button.style.border = "1px solid var(--border, #ebebeb)";
  button.style.background = "var(--popover, #ffffff)";
  button.style.color = "var(--popover-foreground, #17191d)";
  button.style.boxShadow = "0 1px 2px rgb(0 0 0 / 6%), 0 2px 8px rgb(0 0 0 / 8%)";
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
    screenX: number;
    screenY: number;
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
    if (windowManaged) return;
    const horizontalInset = Math.min(dockInset, maxLeft() / 2);
    const verticalInset = Math.min(dockInset, maxTop() / 2);
    button.style.left = "auto";
    button.style.right = `${horizontalInset}px`;
    button.style.top = "auto";
    button.style.bottom = `${verticalInset}px`;
  };
  const endPointer = (pointerId: number, suppressNextClick: boolean, cancelled = false) => {
    const pointer = activePointer;
    if (!pointer || pointer.id !== pointerId) return;
    activePointer = null;
    button.style.cursor = "grab";
    options.onDrag?.({
      phase: cancelled ? "cancel" : "end",
      screenX: pointer.screenX,
      screenY: pointer.screenY,
    });
    if (!pointer.moved) return;

    suppressClick = suppressNextClick;
    if (windowManaged) return;

    const rect = button.getBoundingClientRect();
    rememberSize(rect);
    const distanceToLeft = currentLeft;
    const distanceToRight = view.innerWidth - currentLeft - buttonWidth;
    dockSide = distanceToLeft <= distanceToRight ? "left" : "right";
    dockToEdge(dockSide, currentTop);
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
      originX: windowManaged ? event.screenX : event.clientX,
      originY: windowManaged ? event.screenY : event.clientY,
      originLeft: rect.left,
      originTop: rect.top,
      screenX: event.screenX,
      screenY: event.screenY,
      moved: false,
    };
    button.setPointerCapture(event.pointerId);
    options.onDrag?.({ phase: "start", screenX: event.screenX, screenY: event.screenY });
  };
  const onPointerMove = (event: PointerEvent) => {
    const pointer = activePointer;
    if (!pointer || pointer.id !== event.pointerId) return;
    pointer.screenX = event.screenX;
    pointer.screenY = event.screenY;
    const deltaX = (windowManaged ? event.screenX : event.clientX) - pointer.originX;
    const deltaY = (windowManaged ? event.screenY : event.clientY) - pointer.originY;
    if (!pointer.moved && deltaX * deltaX + deltaY * deltaY <= dragThreshold * dragThreshold)
      return;
    pointer.moved = true;
    hasDragged = true;
    button.style.cursor = "grabbing";
    if (windowManaged) {
      options.onDrag?.({ phase: "move", screenX: event.screenX, screenY: event.screenY });
    } else {
      setPosition(
        clamp(pointer.originLeft + deltaX, maxLeft()),
        clamp(pointer.originTop + deltaY, maxTop()),
      );
    }
    event.preventDefault();
  };
  const onPointerUp = (event: PointerEvent) => endPointer(event.pointerId, true);
  const onPointerCancel = (event: PointerEvent) => {
    endPointer(event.pointerId, true, true);
    if (button.hasPointerCapture(event.pointerId)) button.releasePointerCapture(event.pointerId);
  };
  const onLostPointerCapture = (event: PointerEvent) => endPointer(event.pointerId, true, true);
  const onWindowBlur = () => {
    if (!activePointer) return;
    const pointerId = activePointer.id;
    endPointer(pointerId, true, true);
    if (button.hasPointerCapture(pointerId)) button.releasePointerCapture(pointerId);
  };
  const onResize = () => {
    if (windowManaged) return;
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
    if (windowManaged) return;
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
  view.addEventListener("blur", onWindowBlur);
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
      if (activePointer) {
        const pointer = activePointer;
        activePointer = null;
        options.onDrag?.({ phase: "cancel", screenX: pointer.screenX, screenY: pointer.screenY });
        if (button.hasPointerCapture(pointer.id)) button.releasePointerCapture(pointer.id);
      }
      activePointer = null;
      button.removeEventListener("pointerdown", onPointerDown);
      button.removeEventListener("pointermove", onPointerMove);
      button.removeEventListener("pointerup", onPointerUp);
      button.removeEventListener("pointercancel", onPointerCancel);
      button.removeEventListener("lostpointercapture", onLostPointerCapture);
      button.removeEventListener("click", onClick);
      view.removeEventListener("resize", onResize);
      view.removeEventListener("blur", onWindowBlur);
      resizeObserver.disconnect();
      button.remove();
    },
  };
}

export async function createFloatingChat(
  call: <T>(method: string, params?: unknown) => Promise<T>,
  options: FloatingChatOptions = {},
  onContextCleared: (listener: () => void) => { dispose(): void },
): Promise<FloatingChatHandle> {
  let context = normalizeFloatingChatContext(options.context ?? {});
  let disposed = false;
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
      contextSubscription.dispose();
      await send("chat.floating.dispose");
    },
  };
  try {
    await call("chat.floating.register", { context });
  } catch (error) {
    contextSubscription.dispose();
    throw error;
  }
  return handle;
}
