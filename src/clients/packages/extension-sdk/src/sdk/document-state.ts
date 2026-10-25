// 文档会话的状态转换（4.2-4.4 节）：纯函数，输入当前状态与事件，输出新状态与要执行的动作。运行时按
// `sampleNeeds` 只取下一次状态转换要用的适配器取样值（`version()`、`matches()`）并放进事件，这里不调用适配器。
// The state transitions of a document session (sections 4.2-4.4): pure functions from the current state and an event
// to the next state and the actions to perform. Following `sampleNeeds`, the runtime samples only what the next
// transition uses (`version()`, `matches()`) and puts it into the event; nothing here calls the adapter.
import type {
  DialogApi,
  DocumentSaveStatus,
  DocumentState,
  Eol,
  ExtensionError,
  HostContext,
} from "../api";
import { createExtensionError } from "../rpc";

export type DocumentKind = "text" | "binary";
export type DocumentFile = { readonly uri: string; readonly name: string };
export type TextFormat = { readonly encoding: string; readonly bom: boolean; readonly eol: Eol };
export type MessageBoxOptions = Parameters<DialogApi["showMessageBox"]>[0];

/** 从磁盘读到的内容。Content read from disk. */
export type DiskContent =
  | {
      readonly kind: "text";
      readonly text: string;
      readonly format: TextFormat;
      readonly sha256: string;
    }
  | { readonly kind: "binary"; readonly bytes: Uint8Array; readonly sha256: string };

/**
 * 基线：最近一次从磁盘读取或成功写入时的内容哈希、编码与编辑器快照。
 * The baseline: the content hash, encoding and editor snapshot of the last read from disk or successful write.
 */
export type Baseline<Snapshot> = {
  readonly sha256: string;
  /** 二进制文档为 null。Null for binary documents. */
  readonly format: TextFormat | null;
  readonly snapshot: Snapshot;
};

/**
 * 运行时在处理事件之前从适配器取得的值；`sampleNeeds` 没有要求的值为 null。
 * What the runtime samples from the adapter before an event; values `sampleNeeds` did not ask for are null.
 */
export type EditorSample = {
  /** `adapter.version()`。 */
  readonly version: number | null;
  /** `adapter.matches(baseline.snapshot)`。 */
  readonly matchesBaseline: boolean | null;
  /** `adapter.matches(pending.snapshot)`。 */
  readonly matchesPending: boolean | null;
};

/** 下一次状态转换要用的取样值。The samples the next transition uses. */
export type SampleNeeds = { readonly [Key in keyof EditorSample]: boolean };

/** 调用方发起的操作。Operations the caller starts. */
export type DocumentOperation =
  | { readonly type: "save" }
  | { readonly type: "show"; readonly file: DocumentFile }
  | { readonly type: "reload" }
  | { readonly type: "reopenWithEncoding"; readonly encoding: string }
  | { readonly type: "saveWithEncoding"; readonly encoding: string; readonly bom: boolean };

/** 队列中的任务；同一文件的 show 为 `refresh`，其他文件的 show 为 `load`。Queued tasks. */
export type DocumentTask =
  | { readonly type: "load" }
  | { readonly type: "refresh" }
  | { readonly type: "save" }
  | { readonly type: "reload" }
  | { readonly type: "reopenWithEncoding"; readonly encoding: string }
  | { readonly type: "saveWithEncoding"; readonly encoding: string; readonly bom: boolean };

export type QueuedTask = {
  /**
   * 随任务结束的调用方 Promise 的 id；打开时的读取没有调用方，读取期间同一文件的 show 加入其中。
   * Ids of the caller promises that end with the task; the initial read has none, and same-file shows join a load.
   */
  readonly ids: readonly number[];
  /** 排队时的会话代数。The session generation when queued. */
  readonly generation: number;
  readonly task: DocumentTask;
};

/** 正在执行的任务等待的结果。What the running task waits for. */
export type TaskStep =
  | { readonly type: "read" }
  | { readonly type: "refresh"; readonly version: number }
  | { readonly type: "load"; readonly content: DiskContent }
  | { readonly type: "confirm-discard" }
  | { readonly type: "check" }
  | {
      readonly type: "capture";
      readonly encoding: { readonly encoding: string; readonly bom: boolean } | null;
    }
  | {
      readonly type: "write";
      readonly format: TextFormat | null;
      readonly expectedSha256: string | null;
    }
  | { readonly type: "conflict"; readonly format: TextFormat | null }
  | { readonly type: "overwrite"; readonly format: TextFormat | null };

export type RunningTask = QueuedTask & { readonly step: TaskStep };

export type DocumentModel<Snapshot> = {
  readonly kind: DocumentKind;
  readonly file: DocumentFile;
  /** 会话代数，每次切换文件加 1。The session generation, incremented on every file switch. */
  readonly generation: number;
  readonly state: DocumentState;
  readonly baseline: Baseline<Snapshot> | null;
  /** Saving 与 Conflict 中正在写入的快照。The snapshot being written in Saving and Conflict. */
  readonly pending: { readonly snapshot: Snapshot } | null;
  readonly changedOnDisk: boolean;
  readonly error: ExtensionError | null;
  /** 最近一次报告给宿主的未保存标记。The unsaved flag last reported to the host. */
  readonly unsaved: boolean;
  readonly queue: readonly QueuedTask[];
  readonly running: RunningTask | null;
};

/** 运行时交给状态转换的输入，处理时加上取样值。Inputs from the runtime; the sample is added when reduced. */
export type DocumentInput<Snapshot> =
  | { readonly type: "request"; readonly id: number; readonly operation: DocumentOperation }
  | { readonly type: "edited" }
  | { readonly type: "read-succeeded"; readonly content: DiskContent }
  | { readonly type: "read-failed"; readonly error: ExtensionError }
  | { readonly type: "loaded"; readonly snapshot: Snapshot }
  | { readonly type: "load-failed"; readonly error: ExtensionError }
  | { readonly type: "save-checked" }
  | { readonly type: "captured"; readonly snapshot: Snapshot; readonly eol: Eol | null }
  | { readonly type: "write-succeeded"; readonly sha256: string }
  | { readonly type: "write-failed"; readonly error: ExtensionError }
  | { readonly type: "hash-read"; readonly sha256: string }
  | { readonly type: "hash-read-failed"; readonly error: ExtensionError }
  | { readonly type: "message-box-answered"; readonly index: number }
  | { readonly type: "message-box-failed"; readonly error: ExtensionError };

export type DocumentEvent<Snapshot> = DocumentInput<Snapshot> & { readonly editor: EditorSample };

/**
 * 运行时执行的动作。`load`、`check-save`、`capture` 同步完成，结果事件在其他事件之前处理。
 * Actions the runtime performs. `load`, `check-save` and `capture` complete synchronously and their result events are
 * reduced before any other event.
 */
export type DocumentAction<Snapshot> =
  /**
   * 文本文档用 `fs.readTextDocument`（encoding 为 null 时识别编码），二进制文档用 `fs.readFile`。
   * `fs.readTextDocument` for text (a null encoding detects it), `fs.readFile` for binary documents.
   */
  | { readonly type: "read"; readonly uri: string; readonly encoding: string | null }
  /** 用 `fs.readFile` 读取文件并计算内容哈希。Reads the file with `fs.readFile` and hashes it. */
  | { readonly type: "read-hash"; readonly uri: string }
  /** `adapter.load` 后取快照：`loaded` 或 `load-failed`。`adapter.load`, then a snapshot. */
  | { readonly type: "load"; readonly content: DiskContent }
  /** 立即以新的取样值回送 `save-checked`。Sends `save-checked` back at once with a fresh sample. */
  | { readonly type: "check-save" }
  /** 取 `adapter.snapshot()` 与 `adapter.eol?.()`：`captured`。Takes the snapshot and EOL: `captured`. */
  | { readonly type: "capture" }
  /**
   * 序列化快照后写入；`baseline` 为基线快照，交给 `adapter.serialize`；`expectedSha256` 为 null 时创建文件。
   * Serializes and writes; `baseline` is the baseline snapshot handed to `adapter.serialize`; null creates the file.
   */
  | {
      readonly type: "write";
      readonly uri: string;
      readonly snapshot: Snapshot;
      readonly baseline: Snapshot;
      readonly format: TextFormat | null;
      readonly expectedSha256: string | null;
    }
  | { readonly type: "show-message-box"; readonly options: MessageBoxOptions }
  /** 错误通知。An error notification. */
  | { readonly type: "notify"; readonly message: string }
  | { readonly type: "report-unsaved"; readonly unsaved: boolean }
  /** 报告宿主在文件名后显示的保存状态。Reports the save status the host shows after the file name. */
  | { readonly type: "report-save-status"; readonly status: DocumentSaveStatus | null }
  /** 结束调用方的 Promise；error 不为 null 时以它拒绝。Settles the caller's promise, rejecting with a non-null error. */
  | { readonly type: "settle"; readonly id: number; readonly error: ExtensionError | null }
  /** 状态、文件、编码、`changedOnDisk` 或 `error` 变化。The public fields changed. */
  | { readonly type: "changed" };

export type DocumentTransition<Snapshot> = {
  readonly state: DocumentModel<Snapshot>;
  readonly actions: readonly DocumentAction<Snapshot>[];
};

type Actions<Snapshot> = DocumentAction<Snapshot>[];

/** 冲突对话框的按钮下标。Button indexes of the conflict dialog. */
const OVERWRITE = 0;
const RELOAD = 1;
/** 放弃确认的按钮下标。Button index of the discard confirmation. */
const DISCARD = 0;

function conflictMessageBox(name: string): MessageBoxOptions {
  return {
    type: "warning",
    message: `${name} was changed on disk after it was opened.`,
    detail: "Overwrite it with your version, or reload it and discard your changes.",
    buttons: ["Overwrite", "Reload", "Cancel"],
    defaultId: 2,
    cancelId: 2,
  };
}

function discardMessageBox(name: string): MessageBoxOptions {
  return {
    type: "warning",
    message: `Discard unsaved changes to ${name}?`,
    buttons: ["Discard", "Cancel"],
    defaultId: 1,
    cancelId: 1,
  };
}

function saveFailedMessage(name: string, error: ExtensionError): string {
  return error.code === "ERR_UNENCODABLE"
    ? `Could not save ${name}: ${error.message} Use "Save with Encoding…" to choose another encoding.`
    : `Could not save ${name}: ${error.message}`;
}

/** 打开会话：从 Loading 开始，识别编码读取文件。Opens a session: starts in Loading and reads the file. */
export function openDocument<Snapshot>(
  kind: DocumentKind,
  file: DocumentFile,
): DocumentTransition<Snapshot> {
  return {
    state: {
      kind,
      file: { uri: file.uri, name: file.name },
      generation: 0,
      state: "loading",
      baseline: null,
      pending: null,
      changedOnDisk: false,
      error: null,
      unsaved: false,
      queue: [],
      running: { ids: [], generation: 0, task: { type: "load" }, step: { type: "read" } },
    },
    actions: [{ type: "read", uri: file.uri, encoding: null }],
  };
}

export function reduceDocument<Snapshot>(
  model: DocumentModel<Snapshot>,
  event: DocumentEvent<Snapshot>,
): DocumentTransition<Snapshot> {
  const actions: Actions<Snapshot> = [];
  const next = handle(model, event, actions);
  // 未保存标记只在结果变化时报告。The unsaved flag is reported only when it changes.
  if (next.unsaved !== model.unsaved)
    actions.push({ type: "report-unsaved", unsaved: next.unsaved });
  // 保存状态同样只在变化时报告。The save status is likewise reported only when it changes.
  const saveStatus = saveStatusOf(next);
  if (saveStatus !== saveStatusOf(model))
    actions.push({ type: "report-save-status", status: saveStatus });
  if (publicFieldsChanged(model, next)) actions.push({ type: "changed" });
  return { state: next, actions };
}

/**
 * 宿主在文件名后显示的保存状态：读取中与失败时为 null；Saving 为 "saving"；磁盘已变化为 "changed-on-disk"；Clean 为
 * "saved"；Dirty 与 Conflict 为 "unsaved"。
 * The save status the host shows after the file name: null while loading or failed, "saving" in Saving,
 * "changed-on-disk" when the disk changed, "saved" in Clean, and "unsaved" in Dirty and Conflict.
 */
function saveStatusOf<Snapshot>(model: DocumentModel<Snapshot>): DocumentSaveStatus | null {
  const { state, changedOnDisk } = model;
  if (state === "loading" || state === "failed") return null;
  if (state === "saving") return "saving";
  if (changedOnDisk) return "changed-on-disk";
  return state === "clean" ? "saved" : "unsaved";
}

/**
 * `reduceDocument(model, input)` 要用的取样值。Clean 与 Dirty 中的输入只对比基线；Saving、Conflict、Loading 与
 * Failed 中的输入不取样；写入结束或取不到要保存的内容时才对比写入的快照或基线；读取后的载入不与旧基线对比。
 * 编辑版本只在可能开始刷新或核对刷新结果时需要。
 * The samples `reduceDocument(model, input)` uses. Edits in Clean and Dirty compare with the baseline only; edits in
 * Saving, Conflict, Loading and Failed sample nothing; the written snapshot or the baseline is compared only when a
 * write ends or the content to save cannot be taken; a load after a read is not compared with the old baseline. The
 * edit version is needed only where a refresh may start or its result is checked.
 */
export function sampleNeeds<Snapshot>(
  model: DocumentModel<Snapshot>,
  input: DocumentInput<Snapshot>,
): SampleNeeds {
  const refreshQueued = model.queue.some((queued) => queued.task.type === "refresh");
  const needs = (baseline: boolean, pending = false): SampleNeeds => ({
    version: refreshQueued || model.running?.step.type === "refresh",
    matchesBaseline: baseline,
    matchesPending: pending,
  });
  switch (input.type) {
    case "request":
      return {
        version: input.operation.type === "show" && input.operation.file.uri === model.file.uri,
        matchesBaseline: false,
        matchesPending: false,
      };
    case "edited":
      return { version: false, matchesBaseline: isEditable(model), matchesPending: false };
  }
  const running = model.running;
  if (running === null || running.generation !== model.generation) return needs(false);
  const { step } = running;
  switch (input.type) {
    case "save-checked":
      return needs(true);
    case "write-succeeded":
      return needs(false, true);
    case "write-failed":
      return needs(
        step.type === "capture" ||
          (step.type === "write" && !isConflict(input.error, step.expectedSha256)),
      );
    case "message-box-answered":
      return needs(step.type === "conflict" && input.index !== OVERWRITE && input.index !== RELOAD);
    case "message-box-failed":
      return needs(step.type === "conflict");
    case "hash-read-failed":
      return needs(input.error.code !== "ENOENT");
    default:
      return needs(false);
  }
}

/** 保存快捷键需要的按键信息。The key information the save shortcut needs. */
export type ShortcutKey = {
  readonly key: string;
  readonly metaKey: boolean;
  readonly ctrlKey: boolean;
  readonly shiftKey: boolean;
  readonly altKey: boolean;
};

/**
 * 保存快捷键为 `Cmd+S`（macOS）或 `Ctrl+S`（其他平台），按住 Shift 或 Alt 时不触发。
 * The save shortcut is `Cmd+S` on macOS or `Ctrl+S` elsewhere, ignored while Shift or Alt is held.
 */
export function isSaveShortcut(
  event: ShortcutKey,
  platform: HostContext["app"]["platform"],
): boolean {
  const modifier = platform === "darwin" ? event.metaKey : event.ctrlKey;
  return modifier && !event.shiftKey && !event.altKey && event.key.toLowerCase() === "s";
}

function handle<Snapshot>(
  model: DocumentModel<Snapshot>,
  event: DocumentEvent<Snapshot>,
  actions: Actions<Snapshot>,
): DocumentModel<Snapshot> {
  switch (event.type) {
    case "request":
      return request(model, event.id, event.operation, event.editor, actions);
    case "edited":
      // Saving 与 Conflict 中未保存标记始终为 true；Loading 与 Failed 中输入不影响状态。
      // Saving and Conflict keep the unsaved flag; edits do not count in Loading and Failed.
      return isEditable(model) ? editable(model, matchesBaseline(event.editor)) : model;
    default:
      return result(model, event, actions);
  }
}

function request<Snapshot>(
  model: DocumentModel<Snapshot>,
  id: number,
  operation: DocumentOperation,
  editor: EditorSample,
  actions: Actions<Snapshot>,
): DocumentModel<Snapshot> {
  if (
    model.kind === "binary" &&
    (operation.type === "reopenWithEncoding" || operation.type === "saveWithEncoding")
  ) {
    actions.push({
      type: "settle",
      id,
      error: createExtensionError(
        "ERR_UNSUPPORTED_OPERATION",
        "Binary documents have no text encoding.",
      ),
    });
    return model;
  }
  let next = model;
  let task: DocumentTask;
  if (operation.type !== "show") {
    task = operation;
  } else if (operation.file.uri === model.file.uri) {
    // 当前文件正在读取或等待读取时，show 随那次读取结束，不再读取一次。
    // While the current file is being read or waits to be read, the show ends with that read.
    const joined = joinLoad(model, id);
    if (joined !== null) return joined;
    task = { type: "refresh" };
  } else {
    // 宿主切换文件前已经确认放弃修改并清除了未保存状态：代数加 1，原来文件的结果与排队操作作废。
    // The host confirmed discarding and cleared the unsaved state before switching: the generation grows and the
    // results and queued operations of the previous file become void.
    next = {
      ...model,
      file: { uri: operation.file.uri, name: operation.file.name },
      generation: model.generation + 1,
      state: "loading",
      baseline: null,
      pending: null,
      changedOnDisk: false,
      error: null,
      unsaved: false,
    };
    task = { type: "load" };
  }
  next = { ...next, queue: [...next.queue, { ids: [id], generation: next.generation, task }] };
  return startNext(next, editor, actions);
}

/** 把 show 加入当前代数正在执行或排队的 load；没有时为 null。Joins the show to a load of this generation. */
function joinLoad<Snapshot>(
  model: DocumentModel<Snapshot>,
  id: number,
): DocumentModel<Snapshot> | null {
  const isLoad = (queued: QueuedTask) =>
    queued.task.type === "load" && queued.generation === model.generation;
  const join = <Task extends QueuedTask>(queued: Task): Task => ({
    ...queued,
    ids: [...queued.ids, id],
  });
  if (model.running !== null && isLoad(model.running)) {
    return { ...model, running: join(model.running) };
  }
  const index = model.queue.findIndex(isLoad);
  if (index < 0) return null;
  return {
    ...model,
    queue: model.queue.map((queued, at) => (at === index ? join(queued) : queued)),
  };
}

/** 没有正在执行的任务时开始下一个。Starts the next task when none is running. */
function startNext<Snapshot>(
  model: DocumentModel<Snapshot>,
  editor: EditorSample,
  actions: Actions<Snapshot>,
): DocumentModel<Snapshot> {
  let next = model;
  while (next.running === null && next.queue.length > 0) {
    const [queued, ...rest] = next.queue;
    next = { ...next, queue: rest };
    if (queued.generation !== next.generation) settle(queued.ids, null, actions);
    else next = start(next, queued, editor, actions);
  }
  return next;
}

function start<Snapshot>(
  model: DocumentModel<Snapshot>,
  queued: QueuedTask,
  editor: EditorSample,
  actions: Actions<Snapshot>,
): DocumentModel<Snapshot> {
  const run = (target: DocumentModel<Snapshot>, step: TaskStep): DocumentModel<Snapshot> => ({
    ...target,
    running: { ...queued, step },
  });
  const { task } = queued;
  switch (task.type) {
    case "load":
      actions.push({ type: "read", uri: model.file.uri, encoding: null });
      return run(model, { type: "read" });
    case "refresh":
      // 只在 Clean 状态下刷新；记录读取开始时的编辑版本。Refreshes only in Clean, recording the edit version.
      if (model.state !== "clean") return skip(model, queued, actions);
      actions.push({
        type: "read",
        uri: model.file.uri,
        encoding: requireBaseline(model).format?.encoding ?? null,
      });
      return run(model, { type: "refresh", version: version(editor) });
    case "save":
      if (!isEditable(model)) return skip(model, queued, actions);
      actions.push({ type: "check-save" });
      return run(model, { type: "check" });
    case "saveWithEncoding":
      if (!isEditable(model)) return skip(model, queued, actions);
      actions.push({ type: "capture" });
      return run(model, {
        type: "capture",
        encoding: { encoding: task.encoding, bom: task.bom },
      });
    case "reload":
    case "reopenWithEncoding":
      if (model.state === "dirty") {
        actions.push({ type: "show-message-box", options: discardMessageBox(model.file.name) });
        return run(model, { type: "confirm-discard" });
      }
      return reread(run(model, { type: "read" }), rereadEncoding(model, task), actions);
  }
}

/** 不执行任务，直接结束调用方的 Promise。Ends the callers' promises without running the task. */
function skip<Snapshot>(
  model: DocumentModel<Snapshot>,
  queued: QueuedTask,
  actions: Actions<Snapshot>,
): DocumentModel<Snapshot> {
  settle(queued.ids, null, actions);
  return model;
}

function result<Snapshot>(
  model: DocumentModel<Snapshot>,
  event: Exclude<DocumentEvent<Snapshot>, { type: "request" | "edited" }>,
  actions: Actions<Snapshot>,
): DocumentModel<Snapshot> {
  const running = model.running;
  if (running === null) {
    throw new Error(`The document session got ${event.type} while no operation was running.`);
  }
  const { editor } = event;
  // 切换文件后，原来文件的结果作废。After a file switch, results of the previous file are void.
  if (running.generation !== model.generation) return finish(model, null, editor, actions);
  const { step } = running;
  switch (step.type) {
    case "read":
      if (event.type === "read-succeeded") return applyContent(model, event.content, actions);
      if (event.type === "read-failed") return fail(model, event.error, editor, actions);
      break;
    case "refresh":
      if (event.type === "read-succeeded" || event.type === "read-failed") {
        // 会话代数与编辑版本都没有变化、状态仍为 Clean 时，读取结果才有效。
        // The read counts only when neither the generation nor the edit version changed and the state is still Clean.
        const current = version(editor) === step.version && model.state === "clean";
        if (event.type === "read-failed") {
          return current
            ? fail(model, event.error, editor, actions)
            : finish(model, null, editor, actions);
        }
        // 不替换内容时，changedOnDisk 表示磁盘上的内容哈希是否与基线不同。
        // When the content is not replaced, changedOnDisk tells whether the disk hash differs from the baseline.
        const changed = event.content.sha256 !== requireBaseline(model).sha256;
        if (current && changed) return applyContent(model, event.content, actions);
        return finish({ ...model, changedOnDisk: changed }, null, editor, actions);
      }
      break;
    case "load":
      if (event.type === "loaded") {
        return finish(
          {
            ...model,
            state: "clean",
            baseline: {
              sha256: step.content.sha256,
              format: step.content.kind === "text" ? step.content.format : null,
              snapshot: event.snapshot,
            },
            changedOnDisk: false,
            error: null,
            unsaved: false,
          },
          null,
          editor,
          actions,
        );
      }
      if (event.type === "load-failed") return fail(model, event.error, editor, actions);
      break;
    case "confirm-discard":
      if (event.type === "message-box-answered") {
        return event.index === DISCARD
          ? reread(model, rereadEncoding(model, running.task), actions)
          : finish(model, null, editor, actions);
      }
      if (event.type === "message-box-failed") return finish(model, event.error, editor, actions);
      break;
    case "check":
      if (event.type === "save-checked") {
        // 内容与基线相同时不写入。Nothing is written when the content equals the baseline.
        if (matchesBaseline(editor)) return finish(editable(model, true), null, editor, actions);
        const { format } = requireBaseline(model);
        actions.push({ type: "capture" });
        return withStep(model, {
          type: "capture",
          encoding: format === null ? null : { encoding: format.encoding, bom: format.bom },
        });
      }
      break;
    case "capture":
      if (event.type === "captured") {
        const baseline = requireBaseline(model);
        // 换行符取 `adapter.eol?.()`，没有时取基线的换行符。The EOL is `adapter.eol?.()` or the baseline's.
        const format =
          step.encoding === null || baseline.format === null
            ? null
            : { ...step.encoding, eol: event.eol ?? baseline.format.eol };
        return write(
          { ...model, state: "saving", unsaved: true, pending: { snapshot: event.snapshot } },
          format,
          baseline.sha256,
          actions,
        );
      }
      // 适配器不能给出要保存的内容。The adapter could not provide the content to save.
      if (event.type === "write-failed") return saveFailed(model, event.error, editor, actions);
      break;
    case "write":
      if (event.type === "write-succeeded") {
        // 基线更新为写入的快照；未保存标记按写入的快照重新计算。The baseline becomes the written snapshot.
        return finish(
          {
            ...editable(model, matchesPending(editor)),
            baseline: {
              sha256: event.sha256,
              format: step.format,
              snapshot: requirePending(model),
            },
            pending: null,
            changedOnDisk: false,
          },
          null,
          editor,
          actions,
        );
      }
      if (event.type === "write-failed") {
        if (isConflict(event.error, step.expectedSha256)) {
          actions.push({ type: "show-message-box", options: conflictMessageBox(model.file.name) });
          return withStep(
            { ...model, state: "conflict" },
            { type: "conflict", format: step.format },
          );
        }
        return saveFailed(model, event.error, editor, actions);
      }
      break;
    case "conflict":
      if (event.type === "message-box-answered") {
        if (event.index === OVERWRITE) {
          actions.push({ type: "read-hash", uri: model.file.uri });
          return withStep(
            { ...model, state: "saving" },
            { type: "overwrite", format: step.format },
          );
        }
        if (event.index === RELOAD) {
          return reread(
            { ...model, pending: null },
            requireBaseline(model).format?.encoding ?? null,
            actions,
          );
        }
        return finish(
          { ...editable(model, matchesBaseline(editor)), pending: null },
          null,
          editor,
          actions,
        );
      }
      if (event.type === "message-box-failed") {
        return finish(
          { ...editable(model, matchesBaseline(editor)), pending: null },
          event.error,
          editor,
          actions,
        );
      }
      break;
    case "overwrite":
      // 以重新读取得到的内容哈希为写入前提；文件已不存在时创建。The new hash is the precondition; a missing file is created.
      if (event.type === "hash-read") return write(model, step.format, event.sha256, actions);
      if (event.type === "hash-read-failed") {
        return event.error.code === "ENOENT"
          ? write(model, step.format, null, actions)
          : saveFailed(model, event.error, editor, actions);
      }
      break;
  }
  throw new Error(`The document session got ${event.type} while waiting at ${step.type}.`);
}

/** 读到的内容交给适配器。Hands the content read to the adapter. */
function applyContent<Snapshot>(
  model: DocumentModel<Snapshot>,
  content: DiskContent,
  actions: Actions<Snapshot>,
): DocumentModel<Snapshot> {
  actions.push({ type: "load", content });
  return withStep(model, { type: "load", content });
}

/** 进入 Loading 并重新读取。Enters Loading and reads again. */
function reread<Snapshot>(
  model: DocumentModel<Snapshot>,
  encoding: string | null,
  actions: Actions<Snapshot>,
): DocumentModel<Snapshot> {
  actions.push({ type: "read", uri: model.file.uri, encoding });
  return withStep({ ...model, state: "loading", error: null, unsaved: false }, { type: "read" });
}

/** reload 按基线的编码读取，reopenWithEncoding 按指定编码读取。Reload reads as the baseline encoding. */
function rereadEncoding<Snapshot>(
  model: DocumentModel<Snapshot>,
  task: DocumentTask,
): string | null {
  return task.type === "reopenWithEncoding"
    ? task.encoding
    : (model.baseline?.format?.encoding ?? null);
}

/** 把保存中的快照写入文件。Writes the pending snapshot. */
function write<Snapshot>(
  model: DocumentModel<Snapshot>,
  format: TextFormat | null,
  expectedSha256: string | null,
  actions: Actions<Snapshot>,
): DocumentModel<Snapshot> {
  actions.push({
    type: "write",
    uri: model.file.uri,
    snapshot: requirePending(model),
    baseline: requireBaseline(model).snapshot,
    format,
    expectedSha256,
  });
  return withStep(model, { type: "write", format, expectedSha256 });
}

/** 保存失败：通知，未保存标记按基线重新计算。A failed save notifies and recomputes the flag against the baseline. */
function saveFailed<Snapshot>(
  model: DocumentModel<Snapshot>,
  error: ExtensionError,
  editor: EditorSample,
  actions: Actions<Snapshot>,
): DocumentModel<Snapshot> {
  actions.push({ type: "notify", message: saveFailedMessage(model.file.name, error) });
  return finish(
    { ...editable(model, matchesBaseline(editor)), pending: null },
    null,
    editor,
    actions,
  );
}

function fail<Snapshot>(
  model: DocumentModel<Snapshot>,
  error: ExtensionError,
  editor: EditorSample,
  actions: Actions<Snapshot>,
): DocumentModel<Snapshot> {
  return finish(
    { ...model, state: "failed", error, unsaved: false, changedOnDisk: false },
    null,
    editor,
    actions,
  );
}

/** 结束正在执行的任务并开始下一个。Ends the running task and starts the next one. */
function finish<Snapshot>(
  model: DocumentModel<Snapshot>,
  error: ExtensionError | null,
  editor: EditorSample,
  actions: Actions<Snapshot>,
): DocumentModel<Snapshot> {
  settle(model.running?.ids ?? [], error, actions);
  return startNext({ ...model, running: null }, editor, actions);
}

function settle<Snapshot>(
  ids: readonly number[],
  error: ExtensionError | null,
  actions: Actions<Snapshot>,
): void {
  for (const id of ids) actions.push({ type: "settle", id, error });
}

/**
 * 写入期间文件发生了变化：写入前提不成立，或创建文件（expectedSha256 为 null）时文件已存在。
 * The file changed during the write: the precondition failed, or the file exists when it was being created.
 */
function isConflict(error: ExtensionError, expectedSha256: string | null): boolean {
  return error.code === "ERR_FILE_CHANGED" || (error.code === "EEXIST" && expectedSha256 === null);
}

/** 读取 `sampleNeeds` 要求的取样值；没有取样是运行时的错误。Reads a sample `sampleNeeds` asked for. */
function sampled<Value>(value: Value | null, name: keyof EditorSample): Value {
  if (value === null) throw new Error(`The document session needs ${name}, which was not sampled.`);
  return value;
}

function version(editor: EditorSample): number {
  return sampled(editor.version, "version");
}

function matchesBaseline(editor: EditorSample): boolean {
  return sampled(editor.matchesBaseline, "matchesBaseline");
}

function matchesPending(editor: EditorSample): boolean {
  return sampled(editor.matchesPending, "matchesPending");
}

function withStep<Snapshot>(
  model: DocumentModel<Snapshot>,
  step: TaskStep,
): DocumentModel<Snapshot> {
  return { ...model, running: { ...model.running!, step } };
}

function isEditable<Snapshot>(model: DocumentModel<Snapshot>): boolean {
  return model.state === "clean" || model.state === "dirty";
}

/** Clean 与 Dirty 的未保存标记为 `!matches`。Clean and Dirty report `!matches`. */
function editable<Snapshot>(
  model: DocumentModel<Snapshot>,
  matches: boolean,
): DocumentModel<Snapshot> {
  return { ...model, state: matches ? "clean" : "dirty", unsaved: !matches };
}

function requireBaseline<Snapshot>(model: DocumentModel<Snapshot>): Baseline<Snapshot> {
  if (model.baseline === null) throw new Error("The document session has no baseline.");
  return model.baseline;
}

function requirePending<Snapshot>(model: DocumentModel<Snapshot>): Snapshot {
  if (model.pending === null)
    throw new Error("The document session has no snapshot being written.");
  return model.pending.snapshot;
}

function publicFieldsChanged<Snapshot>(
  before: DocumentModel<Snapshot>,
  after: DocumentModel<Snapshot>,
): boolean {
  const formatBefore = before.baseline?.format ?? null;
  const formatAfter = after.baseline?.format ?? null;
  return (
    before.state !== after.state ||
    before.file.uri !== after.file.uri ||
    before.file.name !== after.file.name ||
    before.changedOnDisk !== after.changedOnDisk ||
    before.error !== after.error ||
    formatBefore?.encoding !== formatAfter?.encoding ||
    formatBefore?.bom !== formatAfter?.bom ||
    formatBefore?.eol !== formatAfter?.eol
  );
}
