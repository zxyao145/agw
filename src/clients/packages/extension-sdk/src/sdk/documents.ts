// 文档会话的运行时（第 4 节）：执行 document-state 给出的动作（RPC、适配器、对话框、未保存报告、通知、快捷键），
// 把结果作为事件交回状态转换。这里不做任何决定。
// The document session runtime (section 4): performs the actions document-state returns (RPCs, adapter calls,
// dialogs, unsaved reports, notifications, the shortcut) and feeds the results back as events. It decides nothing.
import type {
  BinaryDocumentAdapter,
  DocumentSaveStatus,
  DocumentSession,
  Eol,
  ExtensionApi,
  ExtensionError,
  TextDocumentAdapter,
} from "../api";
import {
  ExtensionError as ExtensionErrorClass,
  fromExtensionErrorData,
  toExtensionErrorData,
} from "../rpc";
import {
  type DiskContent,
  type DocumentAction,
  type DocumentFile,
  type DocumentInput,
  type DocumentKind,
  type DocumentModel,
  type DocumentOperation,
  type EditorSample,
  isSaveShortcut,
  openDocument,
  reduceDocument,
  sampleNeeds,
  type TextFormat,
} from "./document-state";

/** 一种文档的读写与适配器调用。Reading, writing and adapter calls of one document kind. */
type DocumentPort<Snapshot> = {
  read(uri: string, encoding: string | null): Promise<DiskContent>;
  /** 序列化并写入，返回写入内容的哈希。Serializes and writes; returns the hash of the written content. */
  write(
    uri: string,
    snapshot: Snapshot,
    baseline: Snapshot,
    format: TextFormat | null,
    expectedSha256: string | null,
  ): Promise<string>;
  load(content: DiskContent): void;
  snapshot(): Snapshot;
  matches(snapshot: Snapshot): boolean;
  version(): number;
  eol(): Eol | null;
};

type SessionOptions = { file: DocumentFile; saveShortcut?: boolean };

/**
 * 会话与宿主之间不属于公开 API 的部分：报告保存状态，以及登记未释放的会话（宿主请求重新读取时用到）。
 * The parts between sessions and the host outside the public API: reporting the save status, and the sessions not
 * yet disposed (used when the host asks for a reload).
 */
export type DocumentSessionHost = {
  setSaveStatus(status: DocumentSaveStatus | null): Promise<void>;
  readonly sessions: Set<DocumentSession>;
};

/**
 * 宿主文件名后的 Reload：磁盘内容已变化的会话重新读取，有未保存修改时先确认放弃。
 * The Reload after the host's file name: sessions whose file changed on disk read it again, confirming discarding
 * unsaved changes first.
 */
export function reloadChangedDocuments(host: DocumentSessionHost): void {
  for (const session of host.sessions) {
    if (session.changedOnDisk)
      void session.reload().catch((error: unknown) => console.error(error));
  }
}

export function openTextDocument<Snapshot>(
  api: ExtensionApi,
  host: DocumentSessionHost,
  options: SessionOptions & { adapter: TextDocumentAdapter<Snapshot> },
): DocumentSession {
  const { adapter } = options;
  return openSession<Snapshot>(api, host, "text", options, {
    read: async (uri, encoding) => {
      const document = await api.fs.readTextDocument(
        uri,
        encoding === null ? undefined : { encoding },
      );
      return {
        kind: "text",
        text: document.text,
        format: { encoding: document.encoding, bom: document.bom, eol: document.eol },
        sha256: document.sha256,
      };
    },
    write: async (uri, snapshot, baseline, format, expectedSha256) => {
      const { sha256 } = await api.fs.writeTextDocument(
        uri,
        adapter.serialize(snapshot, baseline),
        {
          ...requireFormat(format),
          expectedSha256,
        },
      );
      return sha256;
    },
    load: (content) => {
      if (content.kind !== "text") throw new Error("A text document read binary content.");
      adapter.load({ text: content.text, eol: content.format.eol });
    },
    snapshot: () => adapter.snapshot(),
    matches: (snapshot) => adapter.matches(snapshot),
    version: () => adapter.version(),
    eol: () => adapter.eol?.() ?? null,
  });
}

export function openBinaryDocument<Snapshot>(
  api: ExtensionApi,
  host: DocumentSessionHost,
  options: SessionOptions & { adapter: BinaryDocumentAdapter<Snapshot> },
): DocumentSession {
  const { adapter } = options;
  return openSession<Snapshot>(api, host, "binary", options, {
    read: async (uri) => {
      const bytes = await api.fs.readFile(uri);
      return { kind: "binary", bytes, sha256: await sha256Of(bytes) };
    },
    write: async (uri, snapshot, baseline, _format, expectedSha256) => {
      const bytes = adapter.serialize(snapshot, baseline);
      // RPC 会转移字节的 buffer，先计算内容哈希。The RPC transfers the buffer, so the hash comes first.
      const sha256 = await sha256Of(bytes);
      await api.fs.writeFile(
        uri,
        bytes,
        expectedSha256 === null ? { overwrite: false } : { overwrite: true, expectedSha256 },
      );
      return sha256;
    },
    load: (content) => {
      if (content.kind !== "binary") throw new Error("A binary document read text content.");
      adapter.load(content.bytes);
    },
    snapshot: () => adapter.snapshot(),
    matches: (snapshot) => adapter.matches(snapshot),
    version: () => adapter.version(),
    eol: () => null,
  });
}

function openSession<Snapshot>(
  api: ExtensionApi,
  host: DocumentSessionHost,
  kind: DocumentKind,
  options: SessionOptions,
  port: DocumentPort<Snapshot>,
): DocumentSession {
  const opened = openDocument<Snapshot>(kind, options.file);
  let model: DocumentModel<Snapshot> = opened.state;
  let disposed = false;
  let draining = false;
  let nextId = 1;
  /** 等待处理的事件；同步动作的结果放在最前面。Events waiting to be reduced; results of synchronous actions go first. */
  const inputs: DocumentInput<Snapshot>[] = [];
  const callers = new Map<number, { resolve(): void; reject(error: ExtensionError): void }>();
  const listeners = new Set<() => void>();

  /** 只取下一次状态转换要用的值。Samples only what the next transition uses. */
  const sample = (input: DocumentInput<Snapshot>): EditorSample => {
    const needs = sampleNeeds(model, input);
    return {
      version: needs.version ? port.version() : null,
      matchesBaseline: needs.matchesBaseline ? port.matches(requireBaseline(model)) : null,
      matchesPending: needs.matchesPending ? port.matches(requirePending(model)) : null,
    };
  };

  const drain = () => {
    if (draining) return;
    draining = true;
    try {
      while (!disposed && inputs.length > 0) {
        const input = inputs.shift()!;
        let editor: EditorSample;
        try {
          editor = sample(input);
        } catch (error) {
          // 写入结束后适配器的对比或版本调用失败：按保存失败处理，基线不变。
          // An adapter comparison or version call after the write failed: it counts as a failed save, keeping the baseline.
          if (input.type !== "write-succeeded") throw error;
          inputs.unshift({ type: "write-failed", error: toExtensionError(error) });
          continue;
        }
        const transition = reduceDocument(model, { ...input, editor });
        model = transition.state;
        for (const action of transition.actions) perform(action);
      }
    } finally {
      draining = false;
    }
  };

  const dispatch = (input: DocumentInput<Snapshot>) => {
    if (disposed) return;
    inputs.push(input);
    drain();
  };

  /** 同步动作的结果在其他事件之前处理。A synchronous action's result is reduced before other events. */
  const dispatchFirst = (input: DocumentInput<Snapshot>) => {
    inputs.unshift(input);
  };

  const perform = (action: DocumentAction<Snapshot>) => {
    if (disposed) return;
    switch (action.type) {
      case "read":
        void port.read(action.uri, action.encoding).then(
          (content) => dispatch({ type: "read-succeeded", content }),
          (error: unknown) => dispatch({ type: "read-failed", error: toExtensionError(error) }),
        );
        return;
      case "read-hash":
        void api.fs
          .readFile(action.uri)
          .then(sha256Of)
          .then(
            (sha256) => dispatch({ type: "hash-read", sha256 }),
            (error: unknown) =>
              dispatch({ type: "hash-read-failed", error: toExtensionError(error) }),
          );
        return;
      case "load": {
        // 适配器不能载入内容或给出快照时会话进入 Failed 并给出错误。
        // An adapter that cannot load or provide a snapshot puts the session in Failed.
        let snapshot: Snapshot;
        try {
          port.load(action.content);
          snapshot = port.snapshot();
        } catch (error) {
          dispatchFirst({ type: "load-failed", error: toExtensionError(error) });
          return;
        }
        dispatchFirst({ type: "loaded", snapshot });
        return;
      }
      case "check-save":
        dispatchFirst({ type: "save-checked" });
        return;
      case "capture": {
        // 适配器不能给出要保存的内容时保存失败。A save fails when the adapter cannot provide its content.
        let captured: DocumentInput<Snapshot>;
        try {
          captured = { type: "captured", snapshot: port.snapshot(), eol: port.eol() };
        } catch (error) {
          dispatchFirst({ type: "write-failed", error: toExtensionError(error) });
          return;
        }
        dispatchFirst(captured);
        return;
      }
      case "write":
        void port
          .write(action.uri, action.snapshot, action.baseline, action.format, action.expectedSha256)
          .then(
            (sha256) => dispatch({ type: "write-succeeded", sha256 }),
            (error: unknown) => dispatch({ type: "write-failed", error: toExtensionError(error) }),
          );
        return;
      case "show-message-box":
        void api.dialog.showMessageBox(action.options).then(
          (index) => dispatch({ type: "message-box-answered", index }),
          (error: unknown) =>
            dispatch({ type: "message-box-failed", error: toExtensionError(error) }),
        );
        return;
      case "notify":
        api.ui.notify({ type: "error", message: action.message });
        return;
      case "report-unsaved":
        void api.workspace
          .setUnsavedChanges(action.unsaved)
          .catch((error: unknown) => console.error(error));
        return;
      case "report-save-status":
        void host.setSaveStatus(action.status).catch((error: unknown) => console.error(error));
        return;
      case "settle": {
        const caller = callers.get(action.id)!;
        callers.delete(action.id);
        if (action.error === null) caller.resolve();
        else caller.reject(action.error);
        return;
      }
      case "changed":
        for (const listener of listeners) listener();
        return;
    }
  };

  const request = (operation: DocumentOperation): Promise<void> => {
    if (disposed) return Promise.reject(new Error("The document session is disposed."));
    const id = nextId++;
    const settled = new Promise<void>((resolve, reject) => callers.set(id, { resolve, reject }));
    dispatch({ type: "request", id, operation });
    return settled;
  };

  const platform = api.context.get().app.platform;
  const onKeyDown = (event: KeyboardEvent) => {
    if (!isSaveShortcut(event, platform)) return;
    event.preventDefault();
    void session.save();
  };

  const session: DocumentSession = {
    get file() {
      return model.file;
    },
    get state() {
      return model.state;
    },
    get format() {
      return model.baseline?.format ?? null;
    },
    get changedOnDisk() {
      return model.changedOnDisk;
    },
    get error() {
      return model.error;
    },
    onDidChange: (listener) => {
      listeners.add(listener);
      return { dispose: () => listeners.delete(listener) };
    },
    edited: () => dispatch({ type: "edited" }),
    save: () => request({ type: "save" }),
    show: (file) => request({ type: "show", file }),
    reload: () => request({ type: "reload" }),
    reopenWithEncoding: (encoding) => request({ type: "reopenWithEncoding", encoding }),
    saveWithEncoding: (encoding, bom) => request({ type: "saveWithEncoding", encoding, bom }),
    dispose: () => {
      if (disposed) return;
      disposed = true;
      host.sessions.delete(session);
      inputs.length = 0;
      if (options.saveShortcut !== false) window.removeEventListener("keydown", onKeyDown, true);
      // 未完成的操作随会话结束。Pending operations end with the session.
      for (const caller of callers.values()) caller.resolve();
      callers.clear();
      listeners.clear();
    },
  };

  if (options.saveShortcut !== false) window.addEventListener("keydown", onKeyDown, true);
  host.sessions.add(session);
  for (const action of opened.actions) perform(action);
  return session;
}

function requireBaseline<Snapshot>(model: DocumentModel<Snapshot>): Snapshot {
  if (model.baseline === null) throw new Error("The document session has no baseline to compare.");
  return model.baseline.snapshot;
}

function requirePending<Snapshot>(model: DocumentModel<Snapshot>): Snapshot {
  if (model.pending === null) throw new Error("The document session has no pending snapshot.");
  return model.pending.snapshot;
}

function requireFormat(format: TextFormat | null): TextFormat {
  if (format === null) throw new Error("A text document is written without a text format.");
  return format;
}

/** RPC 失败为 ExtensionError；适配器抛出的值转换为 ExtensionError。Adapter throws become ExtensionError. */
function toExtensionError(error: unknown): ExtensionError {
  return error instanceof ExtensionErrorClass
    ? error
    : fromExtensionErrorData(toExtensionErrorData(error));
}

/** 内容哈希：SHA-256，64 位小写十六进制。The content hash: SHA-256 as 64 lowercase hex characters. */
async function sha256Of(bytes: Uint8Array): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", bytes as Uint8Array<ArrayBuffer>);
  return Array.from(new Uint8Array(digest), (byte) => byte.toString(16).padStart(2, "0")).join("");
}
