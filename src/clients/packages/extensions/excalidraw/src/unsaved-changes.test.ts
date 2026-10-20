import assert from "node:assert/strict";
import test from "node:test";

import { matchesSnapshot, takeSnapshot } from "./unsaved-changes";

const rectangle = { id: "rect", version: 3, isDeleted: false, x: 10, y: 20 };
const arrow = { id: "arrow", version: 7, isDeleted: false, x: 0, y: 0 };
const image = { id: "image", version: 2, isDeleted: false, fileId: "file-1" };
const file1 = {
  id: "file-1",
  mimeType: "image/png",
  dataURL: "data:image/png;base64,AA==",
  created: 1,
};
const file2 = {
  id: "file-2",
  mimeType: "image/png",
  dataURL: "data:image/png;base64,AQ==",
  created: 2,
};
const WHITE = '{"viewBackgroundColor":"#ffffff"}';
const YELLOW = '{"viewBackgroundColor":"#fff9db"}';

test("takeSnapshot records non-deleted element versions, file ids and the saved appState", () => {
  const snapshot = takeSnapshot(
    [rectangle, { id: "gone", version: 4, isDeleted: true }, image],
    { "file-1": file1 },
    WHITE,
  );
  assert.deepEqual(
    snapshot.elements,
    new Map([
      ["rect", 3],
      ["image", 2],
    ]),
  );
  assert.deepEqual(snapshot.fileIds, new Set(["file-1"]));
  assert.equal(snapshot.savedAppState, WHITE);
});

test("matchesSnapshot ignores view state changes that keep elements, files and the saved appState", () => {
  const snapshot = takeSnapshot([rectangle, arrow], {}, WHITE);
  // 缩放、滚动、选择不改变元素版本与写入文件的 appState 字段。
  // Zoom, scroll and selection keep element versions and the appState fields written to the file.
  assert.equal(matchesSnapshot(snapshot, [rectangle, arrow], {}, WHITE), true);
  assert.equal(matchesSnapshot(snapshot, [arrow, rectangle], {}, WHITE), true);
});

test("matchesSnapshot detects a change to the appState written to the file", () => {
  const snapshot = takeSnapshot([rectangle], {}, WHITE);
  assert.equal(matchesSnapshot(snapshot, [rectangle], {}, YELLOW), false);
});

test("matchesSnapshot detects element edits, additions and deletions", () => {
  const snapshot = takeSnapshot([rectangle, arrow], {}, WHITE);
  assert.equal(
    matchesSnapshot(snapshot, [{ ...rectangle, version: 4, x: 30 }, arrow], {}, WHITE),
    false,
  );
  assert.equal(
    matchesSnapshot(
      snapshot,
      [rectangle, arrow, { id: "new", version: 1, isDeleted: false }],
      {},
      WHITE,
    ),
    false,
  );
  assert.equal(
    matchesSnapshot(snapshot, [rectangle, { ...arrow, version: 8, isDeleted: true }], {}, WHITE),
    false,
  );
});

test("matchesSnapshot matches again when an added element is deleted", () => {
  const snapshot = takeSnapshot([rectangle], {}, WHITE);
  assert.equal(
    matchesSnapshot(snapshot, [rectangle, { id: "draft", version: 5, isDeleted: true }], {}, WHITE),
    true,
  );
});

test("matchesSnapshot compares the set of embedded files", () => {
  const snapshot = takeSnapshot([image], { "file-1": file1, "file-2": file2 }, WHITE);
  assert.equal(
    matchesSnapshot(snapshot, [image], { "file-2": file2, "file-1": file1 }, WHITE),
    true,
  );
  assert.equal(matchesSnapshot(snapshot, [image], { "file-1": file1 }, WHITE), false);
  assert.equal(
    matchesSnapshot(
      snapshot,
      [image],
      { "file-1": file1, "file-3": { ...file2, id: "file-3" } },
      WHITE,
    ),
    false,
  );
});

test("a save of an older scene keeps later edits unsaved", () => {
  const loaded = takeSnapshot([rectangle], {}, WHITE);

  const edited = [{ ...rectangle, version: 4 }];
  assert.equal(matchesSnapshot(loaded, edited, {}, WHITE), false);
  // 保存开始时的场景。The scene when the save starts.
  const saved = takeSnapshot(edited, {}, WHITE);
  // 写入期间继续编辑。Editing continues while the write is in flight.
  const current = [{ ...rectangle, version: 5 }];
  // 写入成功后与保存的快照对比，仍有未保存修改。After the write succeeds, comparing with the saved snapshot still finds unsaved changes.
  assert.equal(matchesSnapshot(saved, current, {}, WHITE), false);

  // 再次保存当前场景后没有未保存修改。Saving the current scene again leaves nothing unsaved.
  assert.equal(matchesSnapshot(takeSnapshot(current, {}, WHITE), current, {}, WHITE), true);
});
