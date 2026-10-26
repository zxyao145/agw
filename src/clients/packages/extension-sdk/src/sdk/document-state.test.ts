import assert from "node:assert/strict";
import test from "node:test";

import { createExtensionError, ExtensionError } from "../rpc";
import {
  type DiskContent,
  type DocumentInput,
  type DocumentModel,
  type DocumentTransition,
  type EditorSample,
  isSaveShortcut,
  openDocument,
  reduceDocument,
  sampleNeeds,
} from "./document-state";

const FILE = { uri: "agw-cloud://local/project/workspace/notes.md", name: "notes.md" };
const OTHER = { uri: "agw-cloud://local/project/workspace/other.md", name: "other.md" };
const HASH_A = "a".repeat(64);
const HASH_B = "b".repeat(64);
const HASH_C = "c".repeat(64);
const UTF8 = { encoding: "utf-8", bom: false, eol: "\n" } as const;

const CONFLICT_BOX = {
  type: "warning",
  message: "notes.md was changed on disk after it was opened.",
  detail: "Overwrite it with your version, or reload it and discard your changes.",
  buttons: ["Overwrite", "Reload", "Cancel"],
  defaultId: 2,
  cancelId: 2,
};
const DISCARD_BOX = {
  type: "warning",
  message: "Discard unsaved changes to notes.md?",
  buttons: ["Discard", "Cancel"],
  defaultId: 1,
  cancelId: 1,
};

function textContent(text: string, sha256: string, encoding = "utf-8"): DiskContent {
  return { kind: "text", text, format: { ...UTF8, encoding }, sha256 };
}

type Sample = { version: number; matchesBaseline: boolean; matchesPending: boolean };

/**
 * 像运行时一样只提供 `sampleNeeds` 要求的取样值，再处理事件；测试给出的值必须是要求的值。
 * Like the runtime, provides only the samples `sampleNeeds` asks for, then reduces; given values must be asked for.
 */
function step(
  model: DocumentModel<string>,
  input: DocumentInput<string>,
  given: Partial<Sample> = {},
): DocumentTransition<string> {
  const needs = sampleNeeds(model, input);
  for (const name of Object.keys(given) as (keyof Sample)[]) {
    assert.ok(needs[name], `${input.type} does not sample ${name}`);
  }
  const editor: EditorSample = {
    version: needs.version ? (given.version ?? 0) : null,
    matchesBaseline: needs.matchesBaseline ? (given.matchesBaseline ?? false) : null,
    matchesPending: needs.matchesPending ? (given.matchesPending ?? false) : null,
  };
  return reduceDocument(model, { ...input, editor });
}

/** 已读取 A 的文本文档。A text document that has read A. */
function cleanDocument(content = textContent("A", HASH_A)): DocumentModel<string> {
  const opened = openDocument<string>("text", FILE);
  assert.deepEqual(opened.actions, [{ type: "read", uri: FILE.uri, encoding: null }]);
  const read = step(opened.state, { type: "read-succeeded", content });
  assert.deepEqual(read.actions, [{ type: "load", content }]);
  const loaded = step(read.state, { type: "loaded", snapshot: "A" });
  assert.deepEqual(loaded.actions, [
    { type: "report-save-status", status: "saved" },
    { type: "changed" },
  ]);
  assert.equal(loaded.state.state, "clean");
  return loaded.state;
}

/** 基线为 A、内容改为 B 的文本文档。A text document with baseline A edited to B. */
function dirtyDocument(): DocumentModel<string> {
  const edited = step(cleanDocument(), { type: "edited" }, { matchesBaseline: false });
  assert.deepEqual(edited.actions, [
    { type: "report-unsaved", unsaved: true },
    { type: "report-save-status", status: "unsaved" },
    { type: "changed" },
  ]);
  assert.equal(edited.state.state, "dirty");
  return edited.state;
}

/** 正在写入 B 的文本文档（操作 id 为 1）。A text document writing B (operation id 1). */
function savingDocument(): DocumentModel<string> {
  const requested = step(dirtyDocument(), {
    type: "request",
    id: 1,
    operation: { type: "save" },
  });
  assert.deepEqual(requested.actions, [{ type: "check-save" }]);
  const checked = step(requested.state, { type: "save-checked" }, { matchesBaseline: false });
  assert.deepEqual(checked.actions, [{ type: "capture" }]);
  const captured = step(checked.state, { type: "captured", snapshot: "B", eol: null });
  assert.deepEqual(captured.actions, [
    {
      type: "write",
      uri: FILE.uri,
      snapshot: "B",
      baseline: "A",
      format: UTF8,
      expectedSha256: HASH_A,
    },
    { type: "report-save-status", status: "saving" },
    { type: "changed" },
  ]);
  assert.equal(captured.state.state, "saving");
  return captured.state;
}

/** 写入 B 遇到 ERR_FILE_CHANGED 后的文本文档。A text document whose write of B met ERR_FILE_CHANGED. */
function conflictDocument(): DocumentModel<string> {
  const failed = step(savingDocument(), {
    type: "write-failed",
    error: createExtensionError("ERR_FILE_CHANGED", "The file changed."),
  });
  assert.deepEqual(failed.actions, [
    { type: "show-message-box", options: CONFLICT_BOX },
    { type: "report-save-status", status: "unsaved" },
    { type: "changed" },
  ]);
  assert.equal(failed.state.state, "conflict");
  assert.equal(failed.state.unsaved, true);
  return failed.state;
}

test("a save does not write when the content equals the baseline", () => {
  const requested = step(cleanDocument(), { type: "request", id: 1, operation: { type: "save" } });
  assert.deepEqual(requested.actions, [{ type: "check-save" }]);
  const checked = step(requested.state, { type: "save-checked" }, { matchesBaseline: true });
  assert.deepEqual(checked.actions, [{ type: "settle", id: 1, error: null }]);
  assert.equal(checked.state.state, "clean");
});

test("edits during the write leave the document dirty after it succeeds", () => {
  // 写入期间的输入不取样。Edits during the write sample nothing.
  const edited = step(savingDocument(), { type: "edited" });
  assert.deepEqual(edited.actions, []);
  assert.equal(edited.state.state, "saving");
  const written = step(
    edited.state,
    { type: "write-succeeded", sha256: HASH_B },
    { matchesPending: false },
  );
  assert.deepEqual(written.actions, [
    { type: "settle", id: 1, error: null },
    { type: "report-save-status", status: "unsaved" },
    { type: "changed" },
  ]);
  assert.equal(written.state.state, "dirty");
  assert.equal(written.state.unsaved, true);
  assert.deepEqual(written.state.baseline, { sha256: HASH_B, format: UTF8, snapshot: "B" });
  assert.equal(written.state.pending, null);
});

test("undoing to the baseline during the write keeps the unsaved flag until the write ends", () => {
  // 基线为 A，写入 B 期间撤销为 A。Baseline A, undone to A while B is written.
  const undone = step(savingDocument(), { type: "edited" });
  assert.deepEqual(undone.actions, []);
  assert.equal(undone.state.unsaved, true);

  // 成功时按写入的 B 计算：编辑器中的 A 与磁盘上的 B 不同。Success compares with B: the editor's A differs.
  const written = step(
    undone.state,
    { type: "write-succeeded", sha256: HASH_B },
    { matchesPending: false },
  );
  assert.equal(
    written.actions.some((action) => action.type === "report-unsaved"),
    false,
  );
  assert.equal(written.state.state, "dirty");
  assert.equal(written.state.unsaved, true);

  // 失败时按基线 A 计算：已保存。Failure compares with baseline A: saved.
  const failed = step(
    undone.state,
    { type: "write-failed", error: createExtensionError("ERR_SERVER", "Server unavailable.") },
    { matchesBaseline: true },
  );
  assert.deepEqual(failed.actions, [
    { type: "notify", message: "Could not save notes.md: Server unavailable." },
    { type: "settle", id: 1, error: null },
    { type: "report-unsaved", unsaved: false },
    { type: "report-save-status", status: "saved" },
    { type: "changed" },
  ]);
  assert.equal(failed.state.state, "clean");
});

test("save, show, reload and the encoding operations run in call order", () => {
  let model = cleanDocument();
  const requests = [
    { type: "save" },
    { type: "show", file: FILE },
    { type: "reload" },
    { type: "reopenWithEncoding", encoding: "gbk" },
    { type: "saveWithEncoding", encoding: "utf-16le", bom: true },
  ] as const;
  const requested = requests.map((operation, index) => {
    const transition = step(model, { type: "request", id: index + 1, operation });
    model = transition.state;
    return transition.actions;
  });
  assert.deepEqual(requested, [[{ type: "check-save" }], [], [], [], []]);

  // save：内容与基线相同，不写入；接着刷新按基线的编码读取。Nothing to save; the refresh reads with the baseline encoding.
  let transition = step(model, { type: "save-checked" }, { matchesBaseline: true });
  assert.deepEqual(transition.actions, [
    { type: "settle", id: 1, error: null },
    { type: "read", uri: FILE.uri, encoding: "utf-8" },
  ]);
  // show：内容哈希不变；接着 reload 重新读取。The hash is unchanged; then reload reads again.
  transition = step(transition.state, {
    type: "read-succeeded",
    content: textContent("A", HASH_A),
  });
  assert.deepEqual(transition.actions, [
    { type: "settle", id: 2, error: null },
    { type: "read", uri: FILE.uri, encoding: "utf-8" },
    { type: "report-save-status", status: null },
    { type: "changed" },
  ]);
  assert.equal(transition.state.state, "loading");
  transition = step(transition.state, {
    type: "read-succeeded",
    content: textContent("A", HASH_A),
  });
  assert.deepEqual(transition.actions, [{ type: "load", content: textContent("A", HASH_A) }]);
  // reload 完成；reopenWithEncoding 按 gbk 读取。Reload is done; reopenWithEncoding reads as gbk.
  transition = step(transition.state, { type: "loaded", snapshot: "A" });
  assert.deepEqual(transition.actions, [
    { type: "settle", id: 3, error: null },
    { type: "read", uri: FILE.uri, encoding: "gbk" },
  ]);
  transition = step(transition.state, {
    type: "read-succeeded",
    content: textContent("A", HASH_A, "gbk"),
  });
  transition = step(transition.state, { type: "loaded", snapshot: "A" });
  assert.deepEqual(transition.actions, [
    { type: "settle", id: 4, error: null },
    { type: "capture" },
    { type: "report-save-status", status: "saved" },
    { type: "changed" },
  ]);
  assert.deepEqual(transition.state.baseline?.format, { ...UTF8, encoding: "gbk" });
  // saveWithEncoding 即使内容不变也按指定的编码与 BOM 写入。saveWithEncoding writes even without edits.
  transition = step(transition.state, { type: "captured", snapshot: "A", eol: null });
  assert.deepEqual(transition.actions, [
    {
      type: "write",
      uri: FILE.uri,
      snapshot: "A",
      baseline: "A",
      format: { encoding: "utf-16le", bom: true, eol: "\n" },
      expectedSha256: HASH_A,
    },
    { type: "report-unsaved", unsaved: true },
    { type: "report-save-status", status: "saving" },
    { type: "changed" },
  ]);
  transition = step(
    transition.state,
    { type: "write-succeeded", sha256: HASH_C },
    { matchesPending: true },
  );
  assert.deepEqual(transition.actions, [
    { type: "settle", id: 5, error: null },
    { type: "report-unsaved", unsaved: false },
    { type: "report-save-status", status: "saved" },
    { type: "changed" },
  ]);
  assert.deepEqual(transition.state.baseline, {
    sha256: HASH_C,
    format: { encoding: "utf-16le", bom: true, eol: "\n" },
    snapshot: "A",
  });
});

test("a save writes with the editor's line ending when the adapter reports one", () => {
  let transition = step(dirtyDocument(), { type: "request", id: 1, operation: { type: "save" } });
  transition = step(transition.state, { type: "save-checked" }, { matchesBaseline: false });
  transition = step(transition.state, { type: "captured", snapshot: "B", eol: "\r\n" });
  assert.deepEqual(transition.actions[0], {
    type: "write",
    uri: FILE.uri,
    snapshot: "B",
    baseline: "A",
    format: { ...UTF8, eol: "\r\n" },
    expectedSha256: HASH_A,
  });
  transition = step(
    transition.state,
    { type: "write-succeeded", sha256: HASH_B },
    { matchesPending: true },
  );
  assert.deepEqual(transition.state.baseline?.format, { ...UTF8, eol: "\r\n" });
});

test("each write carries the baseline snapshot: the loaded one, the written one after success, the same after failure", () => {
  const save = (model: DocumentModel<string>, id: number, snapshot: string) => {
    let transition = step(model, { type: "edited" }, { matchesBaseline: false });
    transition = step(transition.state, { type: "request", id, operation: { type: "save" } });
    transition = step(transition.state, { type: "save-checked" }, { matchesBaseline: false });
    return step(transition.state, { type: "captured", snapshot, eol: null });
  };
  const writeOf = (transition: DocumentTransition<string>) =>
    transition.actions.find((action) => action.type === "write");

  // 读取后的第一次写入：基线为载入的快照。The first write after the read: the loaded snapshot.
  let transition = save(cleanDocument(), 1, "B");
  assert.deepEqual(writeOf(transition), {
    type: "write",
    uri: FILE.uri,
    snapshot: "B",
    baseline: "A",
    format: UTF8,
    expectedSha256: HASH_A,
  });
  transition = step(
    transition.state,
    { type: "write-succeeded", sha256: HASH_B },
    { matchesPending: true },
  );

  // 写入成功后：基线为写入的快照。After a successful write: the written snapshot.
  transition = save(transition.state, 2, "C");
  assert.deepEqual(writeOf(transition), {
    type: "write",
    uri: FILE.uri,
    snapshot: "C",
    baseline: "B",
    format: UTF8,
    expectedSha256: HASH_B,
  });
  transition = step(
    transition.state,
    { type: "write-failed", error: createExtensionError("EIO", "The disk failed.") },
    { matchesBaseline: false },
  );
  assert.equal(transition.state.state, "dirty");

  // 写入失败后：基线不变。After a failed write: the baseline is unchanged.
  transition = save(transition.state, 3, "D");
  assert.deepEqual(writeOf(transition), {
    type: "write",
    uri: FILE.uri,
    snapshot: "D",
    baseline: "B",
    format: UTF8,
    expectedSha256: HASH_B,
  });
});

test("a refresh does not replace content edited during the read and marks the file changed on disk", () => {
  const requested = step(
    cleanDocument(),
    { type: "request", id: 1, operation: { type: "show", file: FILE } },
    { version: 3 },
  );
  assert.deepEqual(requested.actions, [{ type: "read", uri: FILE.uri, encoding: "utf-8" }]);

  // 读取期间输入。Typed during the read.
  const edited = step(requested.state, { type: "edited" }, { matchesBaseline: false });
  const changed = step(
    edited.state,
    { type: "read-succeeded", content: textContent("disk", HASH_B) },
    { version: 4 },
  );
  assert.deepEqual(changed.actions, [
    { type: "settle", id: 1, error: null },
    { type: "report-save-status", status: "changed-on-disk" },
    { type: "changed" },
  ]);
  assert.equal(changed.state.changedOnDisk, true);
  assert.equal(changed.state.state, "dirty");
  assert.equal(changed.state.baseline?.sha256, HASH_A);

  // 内容哈希没有变化时不设置 changedOnDisk。changedOnDisk stays false when the hash is unchanged.
  const unchanged = step(
    edited.state,
    { type: "read-succeeded", content: textContent("A", HASH_A) },
    { version: 4 },
  );
  assert.equal(unchanged.state.changedOnDisk, false);

  // 输入后又撤销回基线：编辑版本变化，仍不替换。Typed and undone: the version changed, so nothing is replaced.
  const undone = step(
    requested.state,
    { type: "read-succeeded", content: textContent("disk", HASH_B) },
    { version: 5 },
  );
  assert.equal(
    undone.actions.some((action) => action.type === "load"),
    false,
  );
  assert.ok(
    undone.actions.some(
      (action) => action.type === "report-save-status" && action.status === "changed-on-disk",
    ),
  );
  assert.equal(undone.state.changedOnDisk, true);
  assert.equal(undone.state.state, "clean");
});

test("a refresh that finds the baseline hash again clears changedOnDisk", () => {
  // Clean，但上一次刷新发现磁盘已变化。Clean, while the previous refresh found the disk changed.
  const refreshing = step(
    cleanDocument(),
    { type: "request", id: 1, operation: { type: "show", file: FILE } },
    { version: 3 },
  );
  const marked = step(
    refreshing.state,
    { type: "read-succeeded", content: textContent("disk", HASH_B) },
    { version: 4 },
  ).state;
  assert.equal(marked.changedOnDisk, true);
  assert.equal(marked.state, "clean");

  // 核对通过，内容哈希与基线相同：不载入，清除 changedOnDisk。The guard passes on the baseline hash.
  const shown = step(
    marked,
    { type: "request", id: 2, operation: { type: "show", file: FILE } },
    { version: 4 },
  );
  const cleared = step(
    shown.state,
    { type: "read-succeeded", content: textContent("A", HASH_A) },
    { version: 4 },
  );
  assert.deepEqual(cleared.actions, [
    { type: "settle", id: 2, error: null },
    { type: "report-save-status", status: "saved" },
    { type: "changed" },
  ]);
  assert.equal(cleared.state.changedOnDisk, false);
  assert.equal(cleared.state.state, "clean");

  // 读取期间有输入时也按内容哈希设置。With an edit during the read, the hash still decides.
  const typed = step(shown.state, { type: "edited" }, { matchesBaseline: false });
  const kept = step(
    typed.state,
    { type: "read-succeeded", content: textContent("A", HASH_A) },
    { version: 5 },
  );
  assert.equal(kept.state.changedOnDisk, false);
  assert.equal(kept.state.state, "dirty");
});

test("entering Failed clears changedOnDisk", () => {
  const refreshing = step(
    cleanDocument(),
    { type: "request", id: 1, operation: { type: "show", file: FILE } },
    { version: 3 },
  );
  const marked = step(
    refreshing.state,
    { type: "read-succeeded", content: textContent("disk", HASH_B) },
    { version: 4 },
  ).state;
  assert.equal(marked.changedOnDisk, true);

  const shown = step(
    marked,
    { type: "request", id: 2, operation: { type: "show", file: FILE } },
    { version: 4 },
  );
  const missing = createExtensionError("ENOENT", "No such file.");
  const failed = step(shown.state, { type: "read-failed", error: missing }, { version: 4 });
  assert.equal(failed.state.state, "failed");
  assert.equal(failed.state.changedOnDisk, false);
});

test("a failed refresh read in Clean enters Failed and is discarded after an edit", () => {
  const requested = step(
    cleanDocument(),
    { type: "request", id: 1, operation: { type: "show", file: FILE } },
    { version: 3 },
  );
  const missing = createExtensionError("ENOENT", "No such file.");
  const failed = step(requested.state, { type: "read-failed", error: missing }, { version: 3 });
  assert.deepEqual(failed.actions, [
    { type: "settle", id: 1, error: null },
    { type: "report-save-status", status: null },
    { type: "changed" },
  ]);
  assert.equal(failed.state.state, "failed");
  assert.equal(failed.state.error, missing);

  const edited = step(requested.state, { type: "edited" }, { matchesBaseline: false });
  const discarded = step(edited.state, { type: "read-failed", error: missing }, { version: 4 });
  assert.deepEqual(discarded.actions, [{ type: "settle", id: 1, error: null }]);
  assert.equal(discarded.state.state, "dirty");
  assert.equal(discarded.state.error, null);
});

test("a same-file show while the file is being read ends with that read", () => {
  // 宿主在 onLoad 之后发送 onShow；打开时只读取一次。The host sends onShow after onLoad; opening reads once.
  const opened = openDocument<string>("text", FILE);
  const shown = step(opened.state, {
    type: "request",
    id: 1,
    operation: { type: "show", file: FILE },
  });
  assert.deepEqual(shown.actions, []);
  const content = textContent("A", HASH_A);
  const read = step(shown.state, { type: "read-succeeded", content });
  assert.deepEqual(read.actions, [{ type: "load", content }]);
  const loaded = step(read.state, { type: "loaded", snapshot: "A" });
  assert.deepEqual(loaded.actions, [
    { type: "settle", id: 1, error: null },
    { type: "report-save-status", status: "saved" },
    { type: "changed" },
  ]);

  // 切换文件后的读取在排队时，同一文件的 show 加入它。A show joins the queued read of a switch.
  const saving = savingDocument();
  const switched = step(saving, {
    type: "request",
    id: 2,
    operation: { type: "show", file: OTHER },
  });
  const again = step(switched.state, {
    type: "request",
    id: 3,
    operation: { type: "show", file: OTHER },
  });
  assert.deepEqual(again.actions, []);
  assert.equal(again.state.queue.length, 1);
  const started = step(again.state, { type: "write-succeeded", sha256: HASH_B });
  assert.deepEqual(started.actions, [
    { type: "settle", id: 1, error: null },
    { type: "read", uri: OTHER.uri, encoding: null },
  ]);
  const otherContent = textContent("other", HASH_C);
  const otherRead = step(started.state, { type: "read-succeeded", content: otherContent });
  const otherLoaded = step(otherRead.state, { type: "loaded", snapshot: "other" });
  assert.deepEqual(otherLoaded.actions, [
    { type: "settle", id: 2, error: null },
    { type: "settle", id: 3, error: null },
    { type: "report-save-status", status: "saved" },
    { type: "changed" },
  ]);
});

test("sampleNeeds asks only for what the next transition uses", () => {
  const none = { version: false, matchesBaseline: false, matchesPending: false };
  const clean = cleanDocument();
  const dirty = dirtyDocument();
  const saving = savingDocument();
  const conflict = conflictDocument();
  const loading = openDocument<string>("text", FILE).state;
  const failed = step(loading, {
    type: "read-failed",
    error: createExtensionError("ERR_NOT_TEXT", "Binary."),
  }).state;

  // 输入：Clean 与 Dirty 只对比基线，其他状态不取样。Edits compare with the baseline in Clean and Dirty only.
  const edited = { type: "edited" } as const;
  assert.deepEqual(sampleNeeds(clean, edited), { ...none, matchesBaseline: true });
  assert.deepEqual(sampleNeeds(dirty, edited), { ...none, matchesBaseline: true });
  for (const model of [saving, conflict, loading, failed]) {
    assert.deepEqual(sampleNeeds(model, edited), none, model.state);
  }

  // 写入结束时才对比：成功对比写入的快照，失败对比基线，冲突不对比。Comparisons happen when the write ends.
  assert.deepEqual(sampleNeeds(saving, { type: "write-succeeded", sha256: HASH_B }), {
    ...none,
    matchesPending: true,
  });
  const failure = createExtensionError("ERR_SERVER", "Server unavailable.");
  assert.deepEqual(sampleNeeds(saving, { type: "write-failed", error: failure }), {
    ...none,
    matchesBaseline: true,
  });
  const changed = createExtensionError("ERR_FILE_CHANGED", "The file changed.");
  assert.deepEqual(sampleNeeds(saving, { type: "write-failed", error: changed }), none);
  assert.deepEqual(sampleNeeds(conflict, { type: "message-box-answered", index: 0 }), none);
  assert.deepEqual(sampleNeeds(conflict, { type: "message-box-answered", index: 1 }), none);
  assert.deepEqual(sampleNeeds(conflict, { type: "message-box-answered", index: 2 }), {
    ...none,
    matchesBaseline: true,
  });

  // 读取后的载入不与旧基线对比。A load after a read is not compared with the old baseline.
  const reloading = step(conflict, { type: "message-box-answered", index: 1 }).state;
  const reloaded = step(reloading, {
    type: "read-succeeded",
    content: textContent("C", HASH_C),
  }).state;
  assert.deepEqual(sampleNeeds(reloaded, { type: "loaded", snapshot: "C" }), none);
  const refreshing = step(
    clean,
    { type: "request", id: 1, operation: { type: "show", file: FILE } },
    { version: 1 },
  ).state;
  const applying = step(
    refreshing,
    { type: "read-succeeded", content: textContent("disk", HASH_B) },
    { version: 1 },
  ).state;
  assert.deepEqual(sampleNeeds(applying, { type: "loaded", snapshot: "disk" }), none);

  // 编辑版本：开始刷新与核对刷新结果时需要。The version starts a refresh and checks its result.
  const show = { type: "request", id: 1, operation: { type: "show", file: FILE } } as const;
  assert.deepEqual(sampleNeeds(clean, show), { ...none, version: true });
  assert.deepEqual(
    sampleNeeds(clean, { type: "request", id: 1, operation: { type: "save" } }),
    none,
  );
  assert.deepEqual(
    sampleNeeds(refreshing, { type: "read-succeeded", content: textContent("A", HASH_A) }),
    { ...none, version: true },
  );
  const checking = step(clean, { type: "request", id: 1, operation: { type: "save" } }).state;
  assert.deepEqual(sampleNeeds(checking, { type: "save-checked" }), {
    ...none,
    matchesBaseline: true,
  });
});

test("a refresh without edits replaces the content and the baseline", () => {
  const requested = step(
    cleanDocument(),
    { type: "request", id: 1, operation: { type: "show", file: FILE } },
    { version: 3 },
  );
  const content = textContent("disk", HASH_B);
  const read = step(requested.state, { type: "read-succeeded", content }, { version: 3 });
  assert.deepEqual(read.actions, [{ type: "load", content }]);
  const loaded = step(read.state, { type: "loaded", snapshot: "disk" });
  assert.deepEqual(loaded.actions, [{ type: "settle", id: 1, error: null }]);
  assert.deepEqual(loaded.state.baseline, { sha256: HASH_B, format: UTF8, snapshot: "disk" });
  assert.equal(loaded.state.state, "clean");

  // Dirty 时同一文件的 show 不读取。A same-file show in Dirty does not read.
  const dirty = step(loaded.state, { type: "edited" }, { matchesBaseline: false });
  const shown = step(dirty.state, {
    type: "request",
    id: 2,
    operation: { type: "show", file: FILE },
  });
  assert.deepEqual(shown.actions, [{ type: "settle", id: 2, error: null }]);
});

test("switching files during a refresh discards the read result", () => {
  const refreshing = step(cleanDocument(), {
    type: "request",
    id: 1,
    operation: { type: "show", file: FILE },
  });
  const switched = step(refreshing.state, {
    type: "request",
    id: 2,
    operation: { type: "show", file: OTHER },
  });
  assert.deepEqual(switched.actions, [
    { type: "report-save-status", status: null },
    { type: "changed" },
  ]);
  assert.equal(switched.state.state, "loading");
  assert.equal(switched.state.generation, 1);
  assert.deepEqual(switched.state.file, OTHER);

  const discarded = step(switched.state, {
    type: "read-succeeded",
    content: textContent("old file", HASH_B),
  });
  assert.deepEqual(discarded.actions, [
    { type: "settle", id: 1, error: null },
    { type: "read", uri: OTHER.uri, encoding: null },
  ]);
  assert.equal(discarded.state.changedOnDisk, false);

  const content = textContent("other", HASH_C);
  const read = step(discarded.state, { type: "read-succeeded", content });
  assert.deepEqual(read.actions, [{ type: "load", content }]);
  const loaded = step(read.state, { type: "loaded", snapshot: "other" });
  assert.deepEqual(loaded.actions, [
    { type: "settle", id: 2, error: null },
    { type: "report-save-status", status: "saved" },
    { type: "changed" },
  ]);
  assert.deepEqual(loaded.state.baseline, { sha256: HASH_C, format: UTF8, snapshot: "other" });
});

test("operations queued before a file switch are not run for the new file", () => {
  const saving = savingDocument();
  const queued = step(saving, { type: "request", id: 2, operation: { type: "reload" } });
  // 宿主已经确认放弃并清除了未保存状态。The host already confirmed discarding and cleared the unsaved state.
  const switched = step(queued.state, {
    type: "request",
    id: 3,
    operation: { type: "show", file: OTHER },
  });
  assert.deepEqual(switched.actions, [
    { type: "report-unsaved", unsaved: false },
    { type: "report-save-status", status: null },
    { type: "changed" },
  ]);
  const written = step(switched.state, { type: "write-succeeded", sha256: HASH_B });
  assert.deepEqual(written.actions, [
    { type: "settle", id: 1, error: null },
    { type: "settle", id: 2, error: null },
    { type: "read", uri: OTHER.uri, encoding: null },
  ]);
  assert.equal(written.state.baseline, null);
});

test("ERR_FILE_CHANGED enters Conflict; Overwrite reads the current hash and writes with it", () => {
  const overwrite = step(conflictDocument(), { type: "message-box-answered", index: 0 });
  assert.deepEqual(overwrite.actions, [
    { type: "read-hash", uri: FILE.uri },
    { type: "report-save-status", status: "saving" },
    { type: "changed" },
  ]);
  assert.equal(overwrite.state.state, "saving");
  const hashed = step(overwrite.state, { type: "hash-read", sha256: HASH_C });
  assert.deepEqual(hashed.actions, [
    {
      type: "write",
      uri: FILE.uri,
      snapshot: "B",
      baseline: "A",
      format: UTF8,
      expectedSha256: HASH_C,
    },
  ]);
  const written = step(
    hashed.state,
    { type: "write-succeeded", sha256: HASH_B },
    { matchesPending: true },
  );
  assert.deepEqual(written.actions, [
    { type: "settle", id: 1, error: null },
    { type: "report-unsaved", unsaved: false },
    { type: "report-save-status", status: "saved" },
    { type: "changed" },
  ]);
  assert.equal(written.state.state, "clean");
  assert.deepEqual(written.state.baseline, { sha256: HASH_B, format: UTF8, snapshot: "B" });
});

test("Overwrite creates the file when it no longer exists and asks again when it changes once more", () => {
  const overwrite = step(conflictDocument(), { type: "message-box-answered", index: 0 });
  const missing = step(overwrite.state, {
    type: "hash-read-failed",
    error: createExtensionError("ENOENT", "No such file."),
  });
  assert.deepEqual(missing.actions, [
    {
      type: "write",
      uri: FILE.uri,
      snapshot: "B",
      baseline: "A",
      format: UTF8,
      expectedSha256: null,
    },
  ]);
  // 创建期间文件又出现了。The file appeared again while it was being created.
  const created = step(missing.state, {
    type: "write-failed",
    error: createExtensionError("EEXIST", "The file exists."),
  });
  assert.deepEqual(created.actions, [
    { type: "show-message-box", options: CONFLICT_BOX },
    { type: "report-save-status", status: "unsaved" },
    { type: "changed" },
  ]);
  assert.equal(created.state.state, "conflict");
});

test("Reload after a conflict reads the file again, and Cancel keeps the unsaved changes", () => {
  const reload = step(conflictDocument(), { type: "message-box-answered", index: 1 });
  assert.deepEqual(reload.actions, [
    { type: "read", uri: FILE.uri, encoding: "utf-8" },
    { type: "report-unsaved", unsaved: false },
    { type: "report-save-status", status: null },
    { type: "changed" },
  ]);
  assert.equal(reload.state.state, "loading");
  assert.equal(reload.state.pending, null);
  const content = textContent("theirs", HASH_C);
  const read = step(reload.state, { type: "read-succeeded", content });
  assert.deepEqual(read.actions, [{ type: "load", content }]);
  const loaded = step(read.state, { type: "loaded", snapshot: "theirs" });
  assert.deepEqual(loaded.actions, [
    { type: "settle", id: 1, error: null },
    { type: "report-save-status", status: "saved" },
    { type: "changed" },
  ]);
  assert.equal(loaded.state.state, "clean");

  // 文件已不存在时进入 Failed。A missing file enters Failed.
  const missing = createExtensionError("ENOENT", "No such file.");
  const failed = step(reload.state, { type: "read-failed", error: missing });
  assert.deepEqual(failed.actions, [{ type: "settle", id: 1, error: null }, { type: "changed" }]);
  assert.equal(failed.state.state, "failed");
  assert.equal(failed.state.error, missing);

  const cancel = step(
    conflictDocument(),
    { type: "message-box-answered", index: 2 },
    { matchesBaseline: false },
  );
  assert.deepEqual(cancel.actions, [{ type: "settle", id: 1, error: null }, { type: "changed" }]);
  assert.equal(cancel.state.state, "dirty");
  assert.equal(cancel.state.unsaved, true);
  assert.equal(cancel.state.pending, null);
});

test("ERR_UNENCODABLE and other write errors return to Dirty and notify", () => {
  const message = 'The character "😀" (U+1F600) at line 2, column 3 cannot be encoded as gbk.';
  const unencodable = step(savingDocument(), {
    type: "write-failed",
    error: createExtensionError("ERR_UNENCODABLE", message),
  });
  assert.deepEqual(unencodable.actions, [
    {
      type: "notify",
      message: `Could not save notes.md: ${message} Use "Save with Encoding…" to choose another encoding.`,
    },
    { type: "settle", id: 1, error: null },
    { type: "report-save-status", status: "unsaved" },
    { type: "changed" },
  ]);
  assert.equal(unencodable.state.state, "dirty");
  assert.equal(unencodable.state.unsaved, true);

  const other = step(savingDocument(), {
    type: "write-failed",
    error: createExtensionError("ERR_PERMISSION_DENIED", "Not allowed."),
  });
  assert.deepEqual(other.actions, [
    { type: "notify", message: "Could not save notes.md: Not allowed." },
    { type: "settle", id: 1, error: null },
    { type: "report-save-status", status: "unsaved" },
    { type: "changed" },
  ]);
  assert.equal(other.state.state, "dirty");
});

test("a save whose content cannot be captured fails like a write error and keeps the changes", () => {
  const requested = step(dirtyDocument(), {
    type: "request",
    id: 1,
    operation: { type: "saveWithEncoding", encoding: "gbk", bom: false },
  });
  assert.deepEqual(requested.actions, [{ type: "capture" }]);
  const failed = step(
    requested.state,
    { type: "write-failed", error: createExtensionError("ERR_UNKNOWN", "No snapshot.") },
    { matchesBaseline: false },
  );
  assert.deepEqual(failed.actions, [
    { type: "notify", message: "Could not save notes.md: No snapshot." },
    { type: "settle", id: 1, error: null },
  ]);
  assert.equal(failed.state.state, "dirty");
  assert.equal(failed.state.unsaved, true);
  assert.equal(failed.state.running, null);
});

test("reopenWithEncoding in Dirty asks first and does not read after Cancel", () => {
  const asked = step(dirtyDocument(), {
    type: "request",
    id: 1,
    operation: { type: "reopenWithEncoding", encoding: "gbk" },
  });
  assert.deepEqual(asked.actions, [{ type: "show-message-box", options: DISCARD_BOX }]);
  const canceled = step(asked.state, { type: "message-box-answered", index: 1 });
  assert.deepEqual(canceled.actions, [{ type: "settle", id: 1, error: null }]);
  assert.equal(canceled.state.state, "dirty");

  const discarded = step(asked.state, { type: "message-box-answered", index: 0 });
  assert.deepEqual(discarded.actions, [
    { type: "read", uri: FILE.uri, encoding: "gbk" },
    { type: "report-unsaved", unsaved: false },
    { type: "report-save-status", status: null },
    { type: "changed" },
  ]);
  assert.equal(discarded.state.state, "loading");
});

test("a failed read enters Failed until reload or reopenWithEncoding reads again", () => {
  const opened = openDocument<string>("text", FILE);
  const unknown = createExtensionError("ERR_UNKNOWN_ENCODING", "Unknown encoding.");
  const failed = step(opened.state, { type: "read-failed", error: unknown });
  assert.deepEqual(failed.actions, [{ type: "changed" }]);
  assert.equal(failed.state.state, "failed");
  assert.equal(failed.state.error, unknown);

  // 输入、保存与同一文件的 show 都不做任何事。Edits, saves and same-file shows do nothing.
  assert.deepEqual(step(failed.state, { type: "edited" }).actions, []);
  assert.deepEqual(
    step(failed.state, { type: "request", id: 1, operation: { type: "save" } }).actions,
    [{ type: "settle", id: 1, error: null }],
  );
  assert.deepEqual(
    step(failed.state, { type: "request", id: 2, operation: { type: "show", file: FILE } }).actions,
    [{ type: "settle", id: 2, error: null }],
  );

  const reopened = step(failed.state, {
    type: "request",
    id: 3,
    operation: { type: "reopenWithEncoding", encoding: "windows-1252" },
  });
  assert.deepEqual(reopened.actions, [
    { type: "read", uri: FILE.uri, encoding: "windows-1252" },
    { type: "changed" },
  ]);
  assert.equal(reopened.state.error, null);
  const content = textContent("café", HASH_A, "windows-1252");
  const read = step(reopened.state, { type: "read-succeeded", content });
  const loaded = step(read.state, { type: "loaded", snapshot: "café" });
  assert.equal(loaded.state.state, "clean");
  assert.deepEqual(loaded.state.baseline?.format, { ...UTF8, encoding: "windows-1252" });

  // 适配器不能载入读到的内容时同样进入 Failed。An adapter that cannot load the content also enters Failed.
  const parse = new ExtensionError({
    message: "Unexpected token.",
    code: null,
    syscall: null,
    path: null,
  });
  const notLoaded = step(read.state, { type: "load-failed", error: parse });
  assert.deepEqual(notLoaded.actions, [
    { type: "settle", id: 3, error: null },
    { type: "changed" },
  ]);
  assert.equal(notLoaded.state.state, "failed");
  assert.equal(notLoaded.state.error, parse);
});

test("binary documents write bytes without a text format and have no encoding operations", () => {
  const opened = openDocument<string>("binary", FILE);
  assert.deepEqual(opened.actions, [{ type: "read", uri: FILE.uri, encoding: null }]);
  const content: DiskContent = { kind: "binary", bytes: new Uint8Array([1, 2]), sha256: HASH_A };
  let transition = step(opened.state, { type: "read-succeeded", content });
  assert.deepEqual(transition.actions, [{ type: "load", content }]);
  transition = step(transition.state, { type: "loaded", snapshot: "A" });
  assert.deepEqual(transition.state.baseline, { sha256: HASH_A, format: null, snapshot: "A" });

  transition = step(transition.state, { type: "edited" }, { matchesBaseline: false });
  transition = step(transition.state, { type: "request", id: 1, operation: { type: "save" } });
  transition = step(transition.state, { type: "save-checked" }, { matchesBaseline: false });
  transition = step(transition.state, { type: "captured", snapshot: "B", eol: null });
  assert.deepEqual(transition.actions[0], {
    type: "write",
    uri: FILE.uri,
    snapshot: "B",
    baseline: "A",
    format: null,
    expectedSha256: HASH_A,
  });
  transition = step(
    transition.state,
    { type: "write-succeeded", sha256: HASH_B },
    { matchesPending: true },
  );
  assert.deepEqual(transition.state.baseline, { sha256: HASH_B, format: null, snapshot: "B" });

  for (const operation of [
    { type: "reopenWithEncoding", encoding: "utf-8" },
    { type: "saveWithEncoding", encoding: "utf-8", bom: false },
  ] as const) {
    const refused = step(transition.state, { type: "request", id: 2, operation });
    assert.deepEqual(refused.actions, [
      {
        type: "settle",
        id: 2,
        error: createExtensionError(
          "ERR_UNSUPPORTED_OPERATION",
          "Binary documents have no text encoding.",
        ),
      },
    ]);
  }
});

test("the save shortcut is Cmd+S on macOS and Ctrl+S elsewhere, without Shift or Alt", () => {
  const key = (
    modifiers: Partial<Record<"metaKey" | "ctrlKey" | "shiftKey" | "altKey", true>>,
  ) => ({
    key: "s",
    metaKey: false,
    ctrlKey: false,
    shiftKey: false,
    altKey: false,
    ...modifiers,
  });
  assert.equal(isSaveShortcut(key({ metaKey: true }), "darwin"), true);
  assert.equal(isSaveShortcut(key({ metaKey: true, shiftKey: true }), "darwin"), false);
  assert.equal(isSaveShortcut(key({ metaKey: true, altKey: true }), "darwin"), false);
  assert.equal(isSaveShortcut(key({ ctrlKey: true }), "darwin"), false);
  for (const platform of ["win32", "linux"] as const) {
    assert.equal(isSaveShortcut(key({ ctrlKey: true }), platform), true, platform);
    assert.equal(isSaveShortcut(key({ ctrlKey: true, shiftKey: true }), platform), false);
    assert.equal(isSaveShortcut(key({ ctrlKey: true, altKey: true }), platform), false);
    assert.equal(isSaveShortcut(key({ metaKey: true }), platform), false);
  }
  assert.equal(isSaveShortcut({ ...key({ metaKey: true }), key: "a" }, "darwin"), false);
});
