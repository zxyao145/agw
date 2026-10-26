// Desktop 的可编辑 Source（设计文档 5.3 节）：Monaco 文本模型加上与 extension 相同的文档会话；读写经 Server 的
// download 与 write，编码转换在主进程，消息框在确认窗口，未保存状态交给 ExtensionHost。
// Desktop's editable Source (design section 5.3): a Monaco text model with the same document session extensions use;
// reads and writes go through the Server's download and write, encoding conversion happens in the main process,
// message boxes show in the confirmation window and the unsaved state goes to ExtensionHost.
import type { CodeTextModel } from "@agw/code-view";
import { loadCodeTextModel } from "@agw/code-view/text-model";
import {
  type DocumentSaveStatus,
  type DocumentSession,
  type DocumentSessionHost,
  type ExtensionError,
  type FileViewerFile,
  fromExtensionErrorData,
  type HostContext,
  isSaveShortcut,
  openDocumentSession,
  reloadChangedDocuments,
  type TextFormat,
} from "@agw/extension-sdk";
import type { SourceDocumentState } from "@agw/projects";
import type { ExtensionIpcResult } from "@desktop/shared/contracts";

import {
  type ExtensionTextCodec,
  type MessageBoxRequest,
  toMessageBoxRequest,
} from "../extensions/extension-bridge-broker";

/** `SourceDocument` 用到的宿主能力。The host capabilities `SourceDocument` uses. */
export type SourceDocumentServices = {
  readFile(uri: string): Promise<Uint8Array>;
  writeFile(
    uri: string,
    data: Uint8Array,
    options: { overwrite: boolean; createParents: boolean; expectedSha256?: string },
  ): Promise<null>;
  codec(): ExtensionTextCodec;
  /** `signal` 中止时撤回消息框。Aborting `signal` withdraws the message box. */
  showMessageBox(request: MessageBoxRequest, signal: AbortSignal): Promise<number>;
  notifyError(message: string): void;
  /** 有未保存修改的文件名，null 表示没有。The file name with unsaved changes, or null. */
  setUnsaved(fileName: string | null): void;
  platform: HostContext["app"]["platform"];
};

function unwrap<T>(result: ExtensionIpcResult<T>): T {
  if (result.ok) return result.value;
  throw fromExtensionErrorData(result.error);
}

function requireFormat(format: TextFormat | null): TextFormat {
  if (format === null) throw new Error("A text document is written without a text format.");
  return format;
}

/** 编码名称，带 BOM 时加 " with BOM"，例如 "UTF-8 with BOM"。The encoding name, e.g. "UTF-8 with BOM". */
function encodingName(format: { encoding: string; bom: boolean }): string {
  return `${format.encoding.toUpperCase()}${format.bom ? " with BOM" : ""}`;
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

export class SourceDocument {
  private readonly session: DocumentSession;
  private readonly host: DocumentSessionHost;
  /** 释放时撤回等待回答的消息框。Withdraws message boxes awaiting answers when disposed. */
  private readonly closed = new AbortController();
  private readonly listeners = new Set<() => void>();
  private saveStatus: DocumentSaveStatus | null = null;
  private unsaved = false;
  private disposed = false;
  private current: SourceDocumentState;

  /** 加载 Monaco 后为 `file` 打开文档。Opens the document for `file` once Monaco is loaded. */
  public static async open(
    file: FileViewerFile,
    services: SourceDocumentServices,
  ): Promise<SourceDocument> {
    return new SourceDocument(await loadCodeTextModel(), file, services);
  }

  private constructor(
    public readonly model: CodeTextModel,
    file: FileViewerFile,
    private readonly services: SourceDocumentServices,
  ) {
    this.host = {
      setSaveStatus: async (status) => {
        this.saveStatus = status;
        this.changed();
      },
      sessions: new Set(),
    };
    this.session = openDocumentSession<number>(
      {
        readFile: (uri) => services.readFile(uri),
        showMessageBox: (options) =>
          services.showMessageBox(toMessageBoxRequest(options), this.closed.signal),
        notifyError: (message) => services.notifyError(message),
        setUnsavedChanges: async (unsaved) => {
          this.unsaved = unsaved;
          services.setUnsaved(unsaved ? this.session.file.name : null);
          this.changed();
        },
      },
      this.host,
      "text",
      // 保存快捷键由 Source 区域处理，只在焦点位于其中时生效。The Source area handles the save shortcut, only while
      // focus is inside it.
      { file: { uri: file.uri, name: file.name }, shortcutPlatform: null },
      {
        read: async (uri, encoding) => {
          const bytes = await services.readFile(uri);
          const decoded = unwrap(await services.codec().decodeText(bytes, encoding ?? undefined));
          return {
            kind: "text",
            text: decoded.text,
            format: { encoding: decoded.encoding, bom: decoded.bom, eol: decoded.eol },
            sha256: decoded.sha256,
          };
        },
        write: async (uri, _snapshot, _baseline, format, expectedSha256) => {
          const encoded = unwrap(
            await services.codec().encodeText(model.serialize(), requireFormat(format)),
          );
          // null 时只创建文件，不覆盖已有文件。null only creates the file and never overwrites one.
          await services.writeFile(
            uri,
            encoded.bytes,
            expectedSha256 === null
              ? { overwrite: false, createParents: false }
              : { overwrite: true, createParents: false, expectedSha256 },
          );
          return encoded.sha256;
        },
        load: (content) => {
          if (content.kind !== "text") throw new Error("A text document read binary content.");
          model.load({ text: content.text, eol: content.format.eol }, this.session.file);
        },
        snapshot: () => model.snapshot(),
        matches: (snapshot) => model.matches(snapshot),
        version: () => model.version(),
        eol: () => model.eol(),
      },
    );
    model.onDidChangeContent(() => this.session.edited());
    this.session.onDidChange(() => this.changed());
    this.current = this.read();
  }

  /** 当前状态；变化后换成新对象。The current state; a change replaces the object. */
  public get state(): SourceDocumentState {
    return this.current;
  }

  public subscribe(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  /**
   * Source 显示 `file`：另一个文件时切换会话（切换前宿主已经确认放弃修改），同一个文件时刷新。
   * Source shows `file`: another file switches the session (the host confirmed discarding before switching), the same
   * file refreshes it.
   */
  public show(file: FileViewerFile): void {
    if (this.disposed) return;
    if (file.uri !== this.session.file.uri) this.model.beginSwitch();
    this.run(this.session.show({ uri: file.uri, name: file.name }));
  }

  public dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.closed.abort();
    this.session.dispose();
    this.model.dispose();
    this.listeners.clear();
    this.services.setUnsaved(null);
  }

  /**
   * 等待会话操作结束；用户取消（`ERR_CANCELED`）时不提示，其他错误用通知显示。读写失败由会话自己通知。
   * Awaits a session operation; a user cancel (`ERR_CANCELED`) is silent, other errors show as notifications. The
   * session notifies read and write failures itself.
   */
  private run(operation: Promise<void>): void {
    void operation.catch((error: unknown) => {
      if ((error as Partial<ExtensionError> | null)?.code === "ERR_CANCELED") return;
      this.services.notifyError(errorMessage(error));
    });
  }

  private changed(): void {
    if (this.disposed) return;
    this.current = this.read();
    for (const listener of this.listeners) listener();
  }

  private read(): SourceDocumentState {
    const { session, services } = this;
    const { format, state, error } = session;
    return {
      model: this.model,
      fileName: session.file.name,
      status: state,
      saveStatus: this.saveStatus,
      unsaved: this.unsaved,
      encodingLabel: format === null || state === "failed" ? null : encodingName(format),
      error: error === null ? null : { code: error.code, message: error.message },
      save: () => this.run(session.save()),
      reload: () => this.run(session.reload()),
      reloadChanged: () => reloadChangedDocuments(this.host),
      reopenWithEncoding: (encoding) => this.run(session.reopenWithEncoding(encoding)),
      saveWithEncoding: (encoding, bom) => this.run(session.saveWithEncoding(encoding, bom)),
      listEncodings: () => services.codec().listTextEncodings(),
      isSaveShortcut: (event) => isSaveShortcut(event, services.platform),
      notifyError: (message) => services.notifyError(message),
    };
  }
}
