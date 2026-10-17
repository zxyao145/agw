import {
  type BrowserWindow,
  type IpcMainEvent,
  MessageChannelMain,
  WebContentsView,
} from "electron";

import type {
  CreateExtensionFrameRequest,
  ExtensionEvent,
  ExtensionFrameLayoutUpdate,
  ExtensionFrameSnapshot,
} from "../../shared/contracts";
import type { HostPermission } from "../../shared/extension-host-permissions";
import type { ExtensionSessionFactory } from "./extension-session";
import { type ExtensionFrameRegistry, type RegisteredFrame } from "./extension-frame-registry";
import { FrameVisibility } from "./extension-frame-visibility";
import { EXTENSION_SCHEME, isNavigationAllowed } from "./extension-network-rules";

export type ExtensionViewManagerOptions = {
  getMainWindow(): BrowserWindow | null;
  registry: ExtensionFrameRegistry;
  sessions: ExtensionSessionFactory;
  preloadPath: string;
  manifestVersion(extensionId: string): number;
  hostPermissions(extensionId: string): readonly HostPermission[];
  emit(event: ExtensionEvent): void;
  /** 旧页面被替换：主进程释放它的监听与请求。An old page was replaced: release its watchers and requests. */
  onPageReplaced(frame: RegisteredFrame, pageId: string): void;
};

type FrameView = {
  frameId: string;
  extensionId: string;
  kind: CreateExtensionFrameRequest["kind"];
  view: WebContentsView;
  visibility: FrameVisibility;
  inWindow: boolean;
  osProcessId: number;
};

const JPEG_QUALITY = 85;

/** 主进程持有的 extension frame。Extension frames owned by the main process. */
export class ExtensionViewManager {
  private readonly frames = new Map<string, FrameView>();
  private readonly terminatedProcesses = new Set<number>();

  public constructor(private readonly options: ExtensionViewManagerOptions) {}

  public get(frameId: string): FrameView | undefined {
    return this.frames.get(frameId);
  }

  public list(): FrameView[] {
    return [...this.frames.values()];
  }

  public create(request: CreateExtensionFrameRequest): void {
    const window = this.options.getMainWindow();
    if (!window || window.isDestroyed()) throw new Error("The main window is not available.");
    if (this.frames.has(request.frameId))
      throw new Error(`Frame ${request.frameId} already exists.`);
    const isBackground = request.kind === "background";
    if (isBackground !== (request.entry === null))
      throw new Error("Only background frames have no entry.");

    const view = new WebContentsView({
      webPreferences: {
        session: this.options.sessions.get(request.extensionId),
        preload: this.options.preloadPath,
        sandbox: true,
        contextIsolation: true,
        nodeIntegration: false,
        nodeIntegrationInSubFrames: false,
        webviewTag: false,
        navigateOnDragDrop: false,
        // background 不在窗口中，关闭节流以保证定时器正常运行。Background is not in the window, so throttling is off.
        backgroundThrottling: !isBackground,
      },
    });
    const contents = view.webContents;
    contents.setWebRTCIPHandlingPolicy("disable_non_proxied_udp");
    contents.setWindowOpenHandler(() => ({ action: "deny" }));

    const frame: FrameView = {
      frameId: request.frameId,
      extensionId: request.extensionId,
      kind: request.kind,
      view,
      visibility: new FrameVisibility(),
      inWindow: !isBackground,
      osProcessId: 0,
    };
    this.frames.set(request.frameId, frame);
    this.options.registry.register({
      frameId: request.frameId,
      extensionId: request.extensionId,
      kind: request.kind,
      viewId: request.viewId,
      webContentsId: contents.id,
    });
    this.attachListeners(frame);

    if (!isBackground) {
      view.setBackgroundColor(request.backgroundColor);
      view.setVisible(false);
      window.contentView.addChildView(view);
    }
    const url = isBackground
      ? `${EXTENSION_SCHEME}://${request.extensionId}/__agw/background.html`
      : `${EXTENSION_SCHEME}://${request.extensionId}/${request.entry}`;
    contents.loadURL(url).catch((error: Error) => {
      if (!this.frames.has(request.frameId)) return;
      this.options.emit({
        type: "frame-failed",
        extensionId: request.extensionId,
        frameId: request.frameId,
        code: "LOAD_FAILED",
        message: error.message,
      });
    });
  }

  /** `agw-extension:hello`：核对 API 版本，生成 pageId 并交付端口。Checks the API version, creates the pageId and hands out ports. */
  public handleHello(event: IpcMainEvent, payload: unknown): void {
    const contents = event.sender;
    if (event.senderFrame === null || event.senderFrame !== contents.mainFrame) return;
    const registered = this.options.registry.findByWebContents(contents.id);
    const frame = registered ? this.frames.get(registered.frameId) : undefined;
    if (!registered || !frame) return;

    const manifestVersion = (payload as { manifestVersion?: unknown } | null)?.manifestVersion;
    const expected = this.options.manifestVersion(registered.extensionId);
    if (manifestVersion !== expected) {
      this.destroy(registered.frameId);
      this.options.emit({
        type: "frame-failed",
        extensionId: registered.extensionId,
        frameId: registered.frameId,
        code: "MANIFEST_VERSION_MISMATCH",
        message: `The page uses Extension API version ${String(manifestVersion)}, but the manifest declares ${expected}.`,
      });
      return;
    }

    const hello = this.options.registry.recordHello(contents.id);
    if (!hello) return;
    if (hello.replacement)
      this.pageReplaced(hello.replacement.frame, hello.replacement.replacedPageId);
    frame.osProcessId = contents.getOSProcessId();

    const window = this.options.getMainWindow();
    if (!window || window.isDestroyed()) return;
    const { port1, port2 } = new MessageChannelMain();
    window.webContents.postMessage(
      "agw:extension-port",
      { extensionId: registered.extensionId, frameId: registered.frameId, pageId: hello.pageId },
      [port1],
    );
    contents.postMessage("agw-extension:port", null, [port2]);
  }

  /** 页面中被 CSP 拒绝的网络请求记录为被拦截的请求。Network requests refused by the page CSP become blocked requests. */
  public handleCspViolation(event: IpcMainEvent, payload: unknown): void {
    const contents = event.sender;
    if (event.senderFrame === null || event.senderFrame !== contents.mainFrame) return;
    const registered = this.options.registry.findByWebContents(contents.id);
    if (!registered) return;
    const { blockedUri, directive } = (payload ?? {}) as {
      blockedUri?: unknown;
      directive?: unknown;
    };
    if (typeof blockedUri !== "string" || typeof directive !== "string") return;
    let url: URL;
    try {
      url = new URL(blockedUri);
    } catch {
      return;
    }
    if (!["http:", "https:", "ws:", "wss:"].includes(url.protocol)) return;
    this.options.emit({
      type: "request-blocked",
      extensionId: registered.extensionId,
      url: blockedUri,
      resourceType: directive,
      reason: "host-not-permitted",
    });
  }

  public updateLayout(updates: readonly ExtensionFrameLayoutUpdate[]): void {
    const window = this.options.getMainWindow();
    if (!window || window.isDestroyed()) return;
    const zoom = window.webContents.getZoomFactor();
    for (const update of updates) {
      const frame = this.frames.get(update.frameId);
      if (!frame || !frame.inWindow) continue;
      frame.view.setBounds({
        x: Math.round(update.bounds.x * zoom),
        y: Math.round(update.bounds.y * zoom),
        width: Math.max(0, Math.round(update.bounds.width * zoom)),
        height: Math.max(0, Math.round(update.bounds.height * zoom)),
      });
      frame.view.setVisible(frame.visibility.setLayoutVisible(update.visible));
    }
  }

  public obscure(frameIds: readonly string[]): Promise<ExtensionFrameSnapshot[]> {
    return Promise.all(
      frameIds.map(async (frameId): Promise<ExtensionFrameSnapshot> => {
        const frame = this.frames.get(frameId);
        if (!frame || !frame.inWindow) return { frameId, snapshot: null };
        const token = frame.visibility.beginObscure();
        if (!frame.view.getVisible()) {
          if (frame.visibility.completeObscure(token))
            frame.view.setVisible(frame.visibility.visible);
          return { frameId, snapshot: null };
        }
        let image: Electron.NativeImage;
        try {
          image = await frame.view.webContents.capturePage();
        } catch {
          return { frameId, snapshot: null };
        }
        if (!this.frames.has(frameId) || !frame.visibility.completeObscure(token)) {
          return { frameId, snapshot: null };
        }
        frame.view.setVisible(frame.visibility.visible);
        return {
          frameId,
          snapshot: `data:image/jpeg;base64,${image.toJPEG(JPEG_QUALITY).toString("base64")}`,
        };
      }),
    );
  }

  public reveal(frameIds: readonly string[]): void {
    for (const frameId of frameIds) {
      const frame = this.frames.get(frameId);
      if (!frame || !frame.inWindow) continue;
      frame.view.setVisible(frame.visibility.reveal());
    }
  }

  public setBackground(color: string): void {
    for (const frame of this.frames.values()) {
      if (frame.inWindow) frame.view.setBackgroundColor(color);
    }
  }

  /** 从窗口移除并关闭 WebContents；不存在时不做任何事情。Removes and closes the WebContents; no-op when missing. */
  public destroy(frameId: string): void {
    const frame = this.frames.get(frameId);
    if (!frame) return;
    this.frames.delete(frameId);
    this.options.registry.unregister(frameId);
    frame.visibility.destroy();
    const window = this.options.getMainWindow();
    if (frame.inWindow && window && !window.isDestroyed()) {
      window.contentView.removeChildView(frame.view);
    }
    if (!frame.view.webContents.isDestroyed()) frame.view.webContents.close();
  }

  public destroyExtension(extensionId: string): void {
    for (const frame of this.frames.values()) {
      if (frame.extensionId === extensionId) this.destroy(frame.frameId);
    }
  }

  public destroyAll(): void {
    for (const frameId of this.frames.keys()) this.destroy(frameId);
  }

  /** 心跳超时或 `unresponsive`：结束该 frame 所在的进程。Heartbeat timeout or `unresponsive`: end the frame's process. */
  public terminateProcess(frameId: string): void {
    const frame = this.frames.get(frameId);
    if (!frame || frame.view.webContents.isDestroyed()) return;
    const processId = frame.view.webContents.getOSProcessId();
    if (processId > 0) this.terminatedProcesses.add(processId);
    frame.view.webContents.forcefullyCrashRenderer();
  }

  public openDevTools(frameId: string): void {
    const frame = this.frames.get(frameId);
    if (frame) frame.view.webContents.openDevTools({ mode: "detach" });
  }

  private pageReplaced(registered: RegisteredFrame, pageId: string): void {
    this.options.onPageReplaced(registered, pageId);
    this.options.emit({
      type: "frame-page-replaced",
      extensionId: registered.extensionId,
      frameId: registered.frameId,
      pageId,
    });
  }

  private attachListeners(frame: FrameView): void {
    const contents = frame.view.webContents;
    const blockNavigation = (url: string, isMainFrame: boolean, preventDefault: () => void) => {
      if (
        isNavigationAllowed(
          frame.extensionId,
          this.options.hostPermissions(frame.extensionId),
          url,
          isMainFrame,
        )
      ) {
        return;
      }
      preventDefault();
      this.options.emit({
        type: "request-blocked",
        extensionId: frame.extensionId,
        url,
        resourceType: isMainFrame ? "mainFrame" : "subFrame",
        reason: "navigation-blocked",
      });
    };
    contents.on("will-navigate", (event) =>
      blockNavigation(event.url, true, () => event.preventDefault()),
    );
    contents.on("will-frame-navigate", (event) => {
      if (event.isMainFrame) return;
      blockNavigation(event.url, false, () => event.preventDefault());
    });
    contents.on("did-navigate", () => {
      const replacement = this.options.registry.recordNavigation(contents.id);
      if (replacement) this.pageReplaced(replacement.frame, replacement.replacedPageId);
      frame.osProcessId = contents.getOSProcessId();
    });
    contents.on("did-fail-load", (_event, errorCode, errorDescription, _url, isMainFrame) => {
      if (!isMainFrame || errorCode === -3 || !this.frames.has(frame.frameId)) return;
      this.options.emit({
        type: "frame-failed",
        extensionId: frame.extensionId,
        frameId: frame.frameId,
        code: "LOAD_FAILED",
        message: `${errorDescription} (${errorCode})`,
      });
    });
    contents.on("unresponsive", () => this.terminateProcess(frame.frameId));
    contents.on("render-process-gone", (_event, details) => {
      if (!this.frames.has(frame.frameId)) return;
      const osProcessId = frame.osProcessId;
      this.options.emit({
        type: "frame-gone",
        extensionId: frame.extensionId,
        frameId: frame.frameId,
        reason: this.terminatedProcesses.has(osProcessId) ? "unresponsive" : details.reason,
        osProcessId,
      });
    });
  }
}
