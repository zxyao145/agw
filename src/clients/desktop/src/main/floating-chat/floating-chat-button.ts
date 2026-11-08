import {
  ipcMain,
  session,
  WebContentsView,
  type BrowserWindow,
  type IpcMainEvent,
  type IpcMainInvokeEvent,
  type WebContents,
} from "electron";

import type {
  FloatingChatButtonDrag,
  FloatingChatButtonRequest,
  FloatingChatButtonViewState,
} from "../../shared/contracts";
import {
  clampPosition,
  dockPosition,
  FLOATING_CHAT_BUTTON_VIEW_SIZE,
  positionAfterResize,
  positionForDrag,
  type FloatingChatClientArea,
  type FloatingChatDockSide,
  type FloatingChatDragStart,
} from "./floating-chat-geometry";
import {
  createFloatingChatProtocolHandler,
  FLOATING_CHAT_ORIGIN,
  FLOATING_CHAT_PAGE_URL,
  FLOATING_CHAT_SCHEME,
} from "./floating-chat-protocol";

const FLOATING_CHAT_PARTITION = "agw-floating-chat";
const BUTTON_STATE_CHANNEL = "agw:floating-chat-button:state";
const BUTTON_GET_STATE_CHANNEL = "agw:floating-chat-button:get-state";
const BUTTON_READY_CHANNEL = "agw:floating-chat-button:ready";
const BUTTON_DRAG_CHANNEL = "agw:floating-chat-button:drag";
const BUTTON_OPEN_CHANNEL = "agw:floating-chat-button:open";
const HOST_UPDATE_CHANNEL = "agw:update-floating-chat-button";

let floatingChatSession: Electron.Session | null = null;
let floatingChatSessionDirectory: string | null = null;

export function prepareFloatingChatSession(pageDirectory: string): Electron.Session {
  if (floatingChatSession !== null) {
    if (floatingChatSessionDirectory !== pageDirectory) {
      throw new Error("The floating Chat session is already bound to another page directory.");
    }
    return floatingChatSession;
  }

  const nextSession = session.fromPartition(FLOATING_CHAT_PARTITION);
  nextSession.protocol.handle(
    FLOATING_CHAT_SCHEME,
    createFloatingChatProtocolHandler(pageDirectory),
  );
  nextSession.webRequest.onBeforeRequest((details, callback) =>
    callback({
      cancel:
        !details.url.startsWith(`${FLOATING_CHAT_ORIGIN}/`) &&
        !details.url.startsWith("devtools://"),
    }),
  );
  nextSession.setPermissionRequestHandler((_contents, _permission, callback) => callback(false));
  floatingChatSession = nextSession;
  floatingChatSessionDirectory = pageDirectory;
  return nextSession;
}

export type FloatingChatButtonViewOptions = {
  getMainWindow(): BrowserWindow | null;
  preloadPath: string;
  pageDirectory: string;
  onOpen(owner: string): void;
};

type IpcEvent = IpcMainEvent | IpcMainInvokeEvent;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function assertRequest(value: unknown): asserts value is FloatingChatButtonRequest {
  if (
    !isRecord(value) ||
    (value.owner !== null && typeof value.owner !== "string") ||
    typeof value.background !== "string" ||
    typeof value.foreground !== "string" ||
    typeof value.border !== "string" ||
    (value.focus !== undefined && typeof value.focus !== "boolean")
  ) {
    throw new Error("The floating Chat button request is invalid.");
  }
}

function isDrag(value: unknown): value is FloatingChatButtonDrag {
  return (
    isRecord(value) &&
    (value.phase === "start" ||
      value.phase === "move" ||
      value.phase === "end" ||
      value.phase === "cancel") &&
    typeof value.screenX === "number" &&
    Number.isFinite(value.screenX) &&
    typeof value.screenY === "number" &&
    Number.isFinite(value.screenY)
  );
}

export class FloatingChatButtonView {
  private view: WebContentsView | null = null;
  private window: BrowserWindow | null = null;
  private owner: string | null = null;
  private ready = false;
  private focusPending = false;
  private hasPosition = false;
  private dockSide: FloatingChatDockSide = "right";
  private dragStart: FloatingChatDragStart | null = null;
  private state: FloatingChatButtonViewState = {
    background: "#ffffff",
    foreground: "#000000",
    border: "#ebebeb",
    x: 0,
    y: 0,
    dragging: false,
  };

  public constructor(private readonly options: FloatingChatButtonViewOptions) {}

  public get webContents(): WebContents | null {
    return this.view?.webContents ?? null;
  }

  public get viewBounds(): Electron.Rectangle | null {
    return this.view?.getBounds() ?? null;
  }

  public update(request: FloatingChatButtonRequest): void {
    this.state = {
      ...this.state,
      background: request.background,
      foreground: request.foreground,
      border: request.border,
    };
    this.owner = request.owner;
    if (request.owner === null) {
      this.reset();
      return;
    }
    if (request.focus) this.focusPending = true;

    this.ensureView();
    this.applyBounds();
    this.publishState();
    this.view?.setVisible(this.ready);
    this.raise();
    if (this.ready && this.focusPending) this.focusWebContents();
  }

  public reset(): void {
    if (this.state.dragging) {
      const area = this.clientAreaOrNull();
      if (area) {
        const docked = dockPosition(this.state, area);
        this.state = { ...this.state, x: docked.x, y: docked.y };
        this.dockSide = docked.side;
      }
    }
    this.owner = null;
    this.focusPending = false;
    this.dragStart = null;
    this.state = { ...this.state, dragging: false };
    this.applyBounds();
    this.view?.setVisible(false);
    this.publishState();
  }

  public raise(): void {
    if (!this.owner || !this.view || !this.window || this.window.isDestroyed()) return;
    this.window.contentView.addChildView(this.view);
    this.view.setVisible(this.ready);
  }

  public getState(): FloatingChatButtonViewState {
    return { ...this.state };
  }

  public assertMainFrame(event: IpcEvent): void {
    const frame = event.senderFrame;
    if (
      !this.view ||
      event.sender !== this.view.webContents ||
      !frame ||
      frame !== event.sender.mainFrame ||
      !frame.url.startsWith(`${FLOATING_CHAT_ORIGIN}/`)
    ) {
      throw new Error("Floating Chat button IPC is only available to its main frame.");
    }
  }

  public pageReady(): void {
    this.ready = true;
    this.publishState();
    this.raise();
    if (this.focusPending) this.focusWebContents();
  }

  public drag(drag: FloatingChatButtonDrag): void {
    if (this.owner === null || !this.view || !this.window || this.window.isDestroyed()) return;
    const area = this.clientArea();
    if (drag.phase === "start") {
      if (this.state.dragging) return;
      this.dragStart = {
        x: this.state.x,
        y: this.state.y,
        screenX: drag.screenX,
        screenY: drag.screenY,
      };
      this.state = { ...this.state, dragging: true };
    } else if (drag.phase === "move") {
      if (!this.dragStart || !this.state.dragging) return;
      const position = positionForDrag(this.dragStart, drag.screenX, drag.screenY, area);
      this.state = { ...this.state, ...position };
      this.dragStart = { ...position, screenX: drag.screenX, screenY: drag.screenY };
    } else {
      if (!this.dragStart || !this.state.dragging) return;
      const position = positionForDrag(this.dragStart, drag.screenX, drag.screenY, area);
      const docked = dockPosition(position, area);
      this.state = { ...this.state, x: docked.x, y: docked.y, dragging: false };
      this.dockSide = docked.side;
      this.dragStart = null;
    }
    this.applyBounds();
    this.publishState();
  }

  public open(): void {
    if (this.owner !== null) this.options.onOpen(this.owner);
  }

  private ensureView(): void {
    const window = this.options.getMainWindow();
    if (!window || window.isDestroyed()) throw new Error("The main window is not available.");
    if (this.view && this.window === window && !this.view.webContents.isDestroyed()) return;
    this.destroyView();

    const area = window.getContentBounds();
    if (!this.hasPosition) {
      this.state = {
        ...this.state,
        x: Math.max(0, Math.floor(area.width) - FLOATING_CHAT_BUTTON_VIEW_SIZE),
        y: Math.max(0, Math.floor(area.height) - FLOATING_CHAT_BUTTON_VIEW_SIZE - 16),
      };
      this.hasPosition = true;
    } else {
      const position = positionAfterResize(this.state, area, this.dockSide);
      this.state = { ...this.state, ...position };
    }

    const view = new WebContentsView({
      webPreferences: {
        preload: this.options.preloadPath,
        session: prepareFloatingChatSession(this.options.pageDirectory),
        sandbox: true,
        contextIsolation: true,
        nodeIntegration: false,
        nodeIntegrationInSubFrames: false,
        webviewTag: false,
        spellcheck: false,
        navigateOnDragDrop: false,
        safeDialogs: true,
      },
    });
    view.setBackgroundColor("#00000000");
    view.setVisible(false);
    view.webContents.setWindowOpenHandler(() => ({ action: "deny" }));
    view.webContents.on("will-navigate", (event) => event.preventDefault());
    view.webContents.on("will-redirect", (event) => event.preventDefault());
    window.contentView.addChildView(view);
    window.on("resize", this.onWindowResize);
    window.on("hide", this.onWindowInterrupted);
    window.on("blur", this.onWindowInterrupted);
    window.on("closed", this.onWindowClosed);
    this.view = view;
    this.window = window;
    this.ready = false;
    this.applyBounds();
    void view.webContents.loadURL(FLOATING_CHAT_PAGE_URL);
  }

  private destroyView(): void {
    const view = this.view;
    const window = this.window;
    this.view = null;
    this.window = null;
    this.ready = false;
    if (!view) return;
    if (window && !window.isDestroyed()) {
      window.off("resize", this.onWindowResize);
      window.off("hide", this.onWindowInterrupted);
      window.off("blur", this.onWindowInterrupted);
      window.off("closed", this.onWindowClosed);
      window.contentView.removeChildView(view);
    }
    if (!view.webContents.isDestroyed()) view.webContents.close();
  }

  private readonly onWindowResize = (): void => {
    if (!this.window || this.window.isDestroyed()) return;
    const area = this.clientArea();
    if (this.state.dragging) {
      const position = clampPosition(this.state, area);
      this.state = { ...this.state, ...position };
      if (this.dragStart) this.dragStart = { ...this.dragStart, ...position };
    } else {
      const position = positionAfterResize(this.state, area, this.dockSide);
      this.state = { ...this.state, ...position };
    }
    this.applyBounds();
    this.publishState();
  };

  private readonly onWindowInterrupted = (): void => {
    if (!this.state.dragging || !this.dragStart) return;
    this.drag({
      phase: "cancel",
      screenX: this.dragStart.screenX,
      screenY: this.dragStart.screenY,
    });
  };

  private readonly onWindowClosed = (): void => {
    const view = this.view;
    this.view = null;
    this.window = null;
    this.ready = false;
    this.owner = null;
    this.focusPending = false;
    this.dragStart = null;
    this.state = { ...this.state, dragging: false };
    if (view && !view.webContents.isDestroyed()) view.webContents.close();
  };

  private clientArea(): FloatingChatClientArea {
    if (!this.window || this.window.isDestroyed())
      throw new Error("The main window is not available.");
    const { width, height } = this.window.getContentBounds();
    return { width, height };
  }

  private clientAreaOrNull(): FloatingChatClientArea | null {
    if (!this.window || this.window.isDestroyed()) return null;
    const { width, height } = this.window.getContentBounds();
    return { width, height };
  }

  private applyBounds(): void {
    if (!this.view || !this.window || this.window.isDestroyed()) return;
    const area = this.clientArea();
    if (this.state.dragging) {
      this.view.setBounds({
        x: 0,
        y: 0,
        width: Math.max(1, Math.floor(area.width)),
        height: Math.max(1, Math.floor(area.height)),
      });
      return;
    }
    this.view.setBounds({
      x: this.state.x,
      y: this.state.y,
      width: FLOATING_CHAT_BUTTON_VIEW_SIZE,
      height: FLOATING_CHAT_BUTTON_VIEW_SIZE,
    });
  }

  private publishState(): void {
    if (this.view && !this.view.webContents.isDestroyed()) {
      this.view.webContents.send(BUTTON_STATE_CHANNEL, this.getState());
    }
  }

  private focusWebContents(): void {
    if (!this.view || this.view.webContents.isDestroyed() || this.owner === null) return;
    this.view.webContents.focus();
    void this.view.webContents.executeJavaScript('document.querySelector("button").focus()');
    this.focusPending = false;
  }
}

export function registerFloatingChatButtonIpc(
  button: FloatingChatButtonView,
  options: {
    assertHost(event: IpcMainInvokeEvent, hostInstanceId: string): void;
  },
): void {
  ipcMain.handle(HOST_UPDATE_CHANNEL, (event, hostInstanceId: unknown, request: unknown) => {
    if (typeof hostInstanceId !== "string") throw new Error("The host instance id is invalid.");
    options.assertHost(event, hostInstanceId);
    assertRequest(request);
    button.update(request);
  });
  ipcMain.handle(BUTTON_GET_STATE_CHANNEL, (event) => {
    button.assertMainFrame(event);
    return button.getState();
  });
  ipcMain.on(BUTTON_READY_CHANNEL, (event) => {
    button.assertMainFrame(event);
    button.pageReady();
  });
  ipcMain.on(BUTTON_DRAG_CHANNEL, (event, drag: unknown) => {
    button.assertMainFrame(event);
    if (!isDrag(drag)) throw new Error("The floating Chat button drag request is invalid.");
    button.drag(drag);
  });
  ipcMain.on(BUTTON_OPEN_CHANNEL, (event) => {
    button.assertMainFrame(event);
    button.open();
  });
}
