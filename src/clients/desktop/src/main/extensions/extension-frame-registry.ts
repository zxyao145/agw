import { randomUUID } from "node:crypto";

import type { ExtensionFrameKind } from "../../shared/extension-api";
import { ExtensionOperationError } from "./extension-ipc-result";

export type RegisteredFrame = {
  frameId: string;
  extensionId: string;
  kind: ExtensionFrameKind;
  viewId: string | null;
  webContentsId: number;
  /** 当前页面；页面被替换后、新页面握手前为 null。The current page; null between replacement and the new page's handshake. */
  pageId: string | null;
  /** 当前页面对应的文档序号。The document index of the current page. */
  pageDocument: number;
  /** 收到的 `did-navigate` 次数。Number of `did-navigate` signals. */
  commits: number;
  /** 收到的 `hello` 次数，每个文档发送一次。Number of `hello` signals, one per document. */
  hellos: number;
};

export type PageReplacement = { frame: RegisteredFrame; replacedPageId: string };

/**
 * 主进程中 frame、页面与宿主页面的登记。
 * Main-process registration of frames, pages and the host page.
 *
 * 第 k 个文档的提交（`did-navigate`）与握手（`hello`）可能以任意顺序到达；
 * 先到的信号让第 k-1 个文档的页面作废，因此每个页面只被替换一次。
 * Document k's commit (`did-navigate`) and handshake (`hello`) may arrive in any order;
 * whichever comes first retires document k-1's page, so each page is replaced exactly once.
 */
export class ExtensionFrameRegistry {
  private host: string | null = null;
  private readonly frames = new Map<string, RegisteredFrame>();
  private readonly byWebContents = new Map<number, string>();

  public constructor(private readonly createPageId: () => string = randomUUID) {}

  public get hostInstanceId(): string | null {
    return this.host;
  }

  /** 同一值不做任何事；已有其他登记时需要先清理。Same value is a no-op; another registration requires a reset first. */
  public attachState(hostInstanceId: string): "unchanged" | "free" | "reset-required" {
    if (this.host === hostInstanceId) return "unchanged";
    return this.host === null ? "free" : "reset-required";
  }

  public setHost(hostInstanceId: string): void {
    this.host = hostInstanceId;
  }

  public clearHost(): void {
    this.host = null;
  }

  public isCurrentHost(hostInstanceId: string): boolean {
    return this.host !== null && this.host === hostInstanceId;
  }

  public assertHost(hostInstanceId: string): void {
    if (!this.isCurrentHost(hostInstanceId)) {
      throw new Error("This extension host is no longer attached.");
    }
  }

  public register(
    frame: Pick<RegisteredFrame, "frameId" | "extensionId" | "kind" | "viewId" | "webContentsId">,
  ): RegisteredFrame {
    if (this.frames.has(frame.frameId)) throw new Error(`Frame ${frame.frameId} already exists.`);
    const registered: RegisteredFrame = {
      ...frame,
      pageId: null,
      pageDocument: 0,
      commits: 0,
      hellos: 0,
    };
    this.frames.set(frame.frameId, registered);
    this.byWebContents.set(frame.webContentsId, frame.frameId);
    return registered;
  }

  public unregister(frameId: string): RegisteredFrame | undefined {
    const frame = this.frames.get(frameId);
    if (!frame) return undefined;
    this.frames.delete(frameId);
    this.byWebContents.delete(frame.webContentsId);
    return frame;
  }

  public get(frameId: string): RegisteredFrame | undefined {
    return this.frames.get(frameId);
  }

  public findByWebContents(webContentsId: number): RegisteredFrame | undefined {
    const frameId = this.byWebContents.get(webContentsId);
    return frameId === undefined ? undefined : this.frames.get(frameId);
  }

  public framesOf(extensionId: string): RegisteredFrame[] {
    return [...this.frames.values()].filter((frame) => frame.extensionId === extensionId);
  }

  public all(): RegisteredFrame[] {
    return [...this.frames.values()];
  }

  /** 顶层文档的跨文档导航已经提交。A top-level cross-document navigation committed. */
  public recordNavigation(webContentsId: number): PageReplacement | null {
    const frame = this.findByWebContents(webContentsId);
    if (!frame) return null;
    frame.commits += 1;
    return this.retireOlderPage(frame, frame.commits);
  }

  /** 新文档的脚本发来握手。The new document's script sent its handshake. */
  public recordHello(
    webContentsId: number,
  ): { frame: RegisteredFrame; replacement: PageReplacement | null; pageId: string } | null {
    const frame = this.findByWebContents(webContentsId);
    if (!frame) return null;
    frame.hellos += 1;
    const replacement = this.retireOlderPage(frame, frame.hellos);
    frame.pageId = this.createPageId();
    frame.pageDocument = frame.hellos;
    return { frame, replacement, pageId: frame.pageId };
  }

  public isCurrentPage(frameId: string, pageId: string): boolean {
    return this.frames.get(frameId)?.pageId === pageId;
  }

  /** 能力请求必须来自该 extension 当前存活 frame 的当前页面。Capability requests must come from a live frame's current page. */
  public validateCaller(caller: {
    extensionId: string;
    frameId: string;
    pageId: string;
  }): RegisteredFrame {
    const frame = this.frames.get(caller.frameId);
    if (!frame || frame.extensionId !== caller.extensionId || frame.pageId !== caller.pageId) {
      throw new ExtensionOperationError(
        "ERR_FRAME_GONE",
        "The calling frame or page no longer exists.",
      );
    }
    return frame;
  }

  private retireOlderPage(frame: RegisteredFrame, document: number): PageReplacement | null {
    if (frame.pageId === null || frame.pageDocument >= document) return null;
    const replacedPageId = frame.pageId;
    frame.pageId = null;
    return { frame, replacedPageId };
  }
}
