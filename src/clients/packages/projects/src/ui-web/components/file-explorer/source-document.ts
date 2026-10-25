// Desktop 交给 Files 视图的可编辑 Source（设计文档 5.3、5.4、第 7 节）。Web 没有它，Source 只读。
// The editable Source Desktop hands to the Files view (design sections 5.3, 5.4 and 7). Web has none, so its Source is
// read-only.
import type { CodeTextModel } from "@agw/code-view";

import type { FileViewerSaveStatus } from "./file-content";

/** 文档会话的状态，与 extension 的文档会话相同。The document session state, the same as extension sessions'. */
export type SourceDocumentStatus = "loading" | "clean" | "dirty" | "saving" | "conflict" | "failed";

/** 编码列表中的一项。An entry of the encoding list. */
export interface SourceEncoding {
  id: string;
  label: string;
}

/** 保存快捷键判断用到的按键字段。The key fields the save shortcut check uses. */
export interface SourceShortcutKey {
  key: string;
  metaKey: boolean;
  ctrlKey: boolean;
  shiftKey: boolean;
  altKey: boolean;
}

/** 一个文件的可编辑文档的当前状态与操作。The current state and operations of one file's editable document. */
export interface SourceDocumentState {
  model: CodeTextModel;
  /** 会话正在使用的文件名。The file name the session is on. */
  fileName: string;
  status: SourceDocumentStatus;
  /** 文件名后的保存状态；读取中与失败时为 null。The save status after the file name; null while loading or failed. */
  saveStatus: FileViewerSaveStatus | null;
  /** 有未保存修改。Has unsaved changes. */
  unsaved: boolean;
  /** 编码名称，例如 "UTF-8 with BOM"；读取中或失败时为 null。The encoding name, e.g. "UTF-8 with BOM"; null while loading or failed. */
  encodingLabel: string | null;
  /** 使会话进入 Failed 的错误。The error that put the session in Failed. */
  error: { code: string | null; message: string } | null;
  save(): void;
  /** 重新读取，有未保存修改时先确认放弃。Reads again, confirming discarding unsaved changes first. */
  reload(): void;
  /** 文件名后的 Reload：磁盘内容已变化时重新读取。The Reload after the file name: reads again when the disk changed. */
  reloadChanged(): void;
  reopenWithEncoding(encoding: string): void;
  saveWithEncoding(encoding: string, bom: boolean): void;
  listEncodings(): Promise<readonly SourceEncoding[]>;
  isSaveShortcut(event: SourceShortcutKey): boolean;
  notifyError(message: string): void;
}

/**
 * Files 视图中的可编辑 Source：`document` 在文档创建之前为 null；Source 显示时（挂载或切换回来）调用 `show`，宿主在
 * 第一次显示时创建文档，之后每次显示刷新当前文件。
 * The editable Source of the Files view: `document` is null until the document exists; `show` is called whenever
 * Source shows (mounted or switched back to), and the host creates the document on the first show and refreshes the
 * current file on later ones.
 */
export interface EditableSource {
  document: SourceDocumentState | null;
  show(): void;
}
