// 未保存修改的判断：比较未删除元素的版本、嵌入文件与写入文件的 appState 字段（背景颜色、网格设置）；
// 缩放、滚动、选择等视图状态不参与。
// Unsaved-change detection: compares non-deleted element versions, embedded files and the appState fields written to
// the file (background color, grid settings); view state such as zoom, scroll and selection is ignored.

/** 判断所需的元素字段，Excalidraw 元素都满足。The element fields the check needs; every Excalidraw element has them. */
export interface SnapshotElement {
  readonly id: string;
  readonly version: number;
  readonly isDeleted: boolean;
}

/** 以文件 id 为键的嵌入文件（Excalidraw 的 BinaryFiles）。Embedded files keyed by file id (Excalidraw's BinaryFiles). */
export type SnapshotFiles = Readonly<Record<string, unknown>>;

/** 打开或保存时的场景状态。The scene state at open or save time. */
export interface SceneSnapshot {
  /** 未删除元素的 id 到版本。Non-deleted element ids to versions. */
  readonly elements: ReadonlyMap<string, number>;
  readonly fileIds: ReadonlySet<string>;
  /** 写入文件的 appState 字段的序列化结果。The serialized appState fields written to the file. */
  readonly savedAppState: string;
}

export function takeSnapshot(
  elements: readonly SnapshotElement[],
  files: SnapshotFiles,
  savedAppState: string,
): SceneSnapshot {
  const versions = new Map<string, number>();
  for (const element of elements) {
    if (!element.isDeleted) versions.set(element.id, element.version);
  }
  return { elements: versions, fileIds: new Set(Object.keys(files)), savedAppState };
}

/**
 * 当前场景与快照的元素版本、嵌入文件、写入文件的 appState 字段都相同时返回 true。
 * True when element versions, embedded files and the appState fields written to the file equal the snapshot.
 */
export function matchesSnapshot(
  snapshot: SceneSnapshot,
  elements: readonly SnapshotElement[],
  files: SnapshotFiles,
  savedAppState: string,
): boolean {
  if (savedAppState !== snapshot.savedAppState) return false;
  let count = 0;
  for (const element of elements) {
    if (element.isDeleted) continue;
    if (snapshot.elements.get(element.id) !== element.version) return false;
    count += 1;
  }
  if (count !== snapshot.elements.size) return false;
  const fileIds = Object.keys(files);
  return (
    fileIds.length === snapshot.fileIds.size && fileIds.every((id) => snapshot.fileIds.has(id))
  );
}
