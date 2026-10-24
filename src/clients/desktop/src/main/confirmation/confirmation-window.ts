import {
  BrowserWindow,
  ipcMain,
  type IpcMainEvent,
  type IpcMainInvokeEvent,
  nativeTheme,
  session,
} from "electron";

import type { ConfirmationView } from "../../shared/contracts";
import {
  CONFIRMATION_ORIGIN,
  CONFIRMATION_PAGE_URL,
  CONFIRMATION_SCHEME,
  createConfirmationProtocolHandler,
} from "./confirmation-protocol";
import type { ConfirmationService, ConfirmationWindow } from "./confirmation-service";

const WINDOW_WIDTH = 460;
const MIN_HEIGHT = 160;
const MAX_HEIGHT = 640;
const CONFIRMATION_PARTITION = "agw-confirmation";

/**
 * 确认窗口使用的会话：只提供确认页面的资源，拒绝其他请求与权限。
 * The session of confirmation windows: serves only the confirmation page's resources and refuses other requests and
 * permissions.
 */
export function prepareConfirmationSession(pageDirectory: string): Electron.Session {
  const confirmationSession = session.fromPartition(CONFIRMATION_PARTITION);
  confirmationSession.protocol.handle(
    CONFIRMATION_SCHEME,
    createConfirmationProtocolHandler(pageDirectory),
  );
  confirmationSession.webRequest.onBeforeRequest((details, callback) =>
    callback({
      cancel:
        !details.url.startsWith(`${CONFIRMATION_ORIGIN}/`) &&
        !details.url.startsWith("devtools://"),
    }),
  );
  confirmationSession.setPermissionRequestHandler((_contents, _permission, callback) =>
    callback(false),
  );
  return confirmationSession;
}

function titleOf(view: ConfirmationView): string {
  switch (view.kind) {
    case "discard-unsaved":
      return "Discard unsaved changes?";
    case "open-link":
      return "Open link?";
    case "message":
      return view.extensionName;
    case "install":
      return `Install ${view.extensionName}?`;
    case "enable":
      return `Enable ${view.extensionName}?`;
  }
}

/**
 * 创建确认窗口：以主窗口为 parent 的 modal `BrowserWindow`，独立 document 与 preload，启用 sandbox 与 contextIsolation，
 * 禁止导航与新窗口。页面完成绘制之前不显示。
 * Creates a confirmation window: a modal `BrowserWindow` parented to the main window with its own document and
 * preload, sandbox and context isolation on, and navigation and new windows denied. It stays hidden until the page
 * rendered.
 */
export function createConfirmationWindowFactory(options: {
  parent(): BrowserWindow | null;
  preloadPath: string;
  session: Electron.Session;
}): (view: ConfirmationView, onClosed: () => void) => ConfirmationWindow {
  return (view, onClosed) => {
    const parent = options.parent();
    const window = new BrowserWindow({
      ...(parent && !parent.isDestroyed() ? { parent, modal: true } : {}),
      show: false,
      width: WINDOW_WIDTH,
      height: MIN_HEIGHT,
      useContentSize: true,
      resizable: false,
      minimizable: false,
      maximizable: false,
      fullscreenable: false,
      skipTaskbar: true,
      title: titleOf(view),
      backgroundColor: nativeTheme.shouldUseDarkColors ? "#1f1f1f" : "#ffffff",
      webPreferences: {
        preload: options.preloadPath,
        session: options.session,
        sandbox: true,
        contextIsolation: true,
        nodeIntegration: false,
        spellcheck: false,
        navigateOnDragDrop: false,
        safeDialogs: true,
      },
    });
    window.setMenu(null);
    window.webContents.on("will-navigate", (event) => event.preventDefault());
    window.webContents.on("will-redirect", (event) => event.preventDefault());
    window.webContents.setWindowOpenHandler(() => ({ action: "deny" }));
    window.on("closed", onClosed);
    void window.loadURL(CONFIRMATION_PAGE_URL);
    return {
      contents: window.webContents,
      show: (height) => {
        if (window.isDestroyed()) return;
        const content = Math.min(MAX_HEIGHT, Math.max(MIN_HEIGHT, Math.ceil(height)));
        window.setContentSize(WINDOW_WIDTH, content);
        window.show();
      },
      close: () => {
        if (!window.isDestroyed()) window.close();
      },
    };
  };
}

/** 只接受确认页面主 frame 发出的 IPC。Accepts IPC only from the confirmation page's main frame. */
function assertConfirmationFrame(event: IpcMainEvent | IpcMainInvokeEvent): void {
  const frame = event.senderFrame;
  if (
    !frame ||
    frame !== event.sender.mainFrame ||
    !frame.url.startsWith(`${CONFIRMATION_ORIGIN}/`)
  ) {
    throw new Error("Confirmation IPC is only available to the confirmation page.");
  }
}

/** 注册确认窗口的 IPC。Registers the confirmation window IPC. */
export function registerConfirmationIpc(service: ConfirmationService): void {
  ipcMain.handle("agw-confirmation:request", (event) => {
    assertConfirmationFrame(event);
    return service.requestFor(event.sender);
  });
  ipcMain.on("agw-confirmation:ready", (event, requestId: unknown, height: unknown) => {
    try {
      assertConfirmationFrame(event);
      service.ready(event.sender, String(requestId), Number(height));
    } catch (error) {
      console.error("A confirmation window reported an invalid ready message.", error);
    }
  });
  ipcMain.handle("agw-confirmation:answer", async (event, requestId: unknown, answer: unknown) => {
    assertConfirmationFrame(event);
    await service.answer(event.sender, String(requestId), answer);
  });
}
