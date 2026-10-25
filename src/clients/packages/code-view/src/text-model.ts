// 可编辑的文本模型（设计文档 5.1 节）。入口只声明接口，实现与 Monaco 由 loadCodeTextModel 按需加载。
// The editable text model (design section 5.1). The entry declares only the interface; the implementation and Monaco
// load on demand through loadCodeTextModel.

/** 换行。A line break. */
export type CodeEol = "\n" | "\r\n";

/** 模型所属的文件。The file a model belongs to. */
export interface CodeTextFile {
  uri: string;
  name: string;
}

export interface CodeTextDisposable {
  dispose(): void;
}

/**
 * 一个文件的 Monaco 文本模型，供文档会话的适配器与编辑器共用。同一个文件重新读入时 `setValue`，换了文件时创建新模型；
 * 快照为 `getAlternativeVersionId()`，撤销到基线时恢复为已保存。
 * One file's Monaco text model, shared by the document session's adapter and the editor. Reading the same file again
 * uses `setValue`, and another file gets a new model; snapshots are `getAlternativeVersionId()`, so undoing to the
 * baseline reads as saved.
 */
export interface CodeTextModel {
  /**
   * 读入内容；`file` 与当前文件不同，或者调用过 `beginSwitch` 时换成新模型。
   * Loads content; the model is replaced when `file` differs from the current file or after `beginSwitch`.
   */
  load(content: { text: string; eol: CodeEol }, file: CodeTextFile): void;
  /**
   * 文档会话切换到另一个文件时调用：下一次读入总是创建新模型，即使又回到模型所属的文件（例如另一个文件读取失败后）。
   * Called when the document session switches to another file: the next load always creates a new model, even when
   * it returns to the model's file (for example after another file failed to read).
   */
  beginSwitch(): void;
  snapshot(): number;
  matches(snapshot: number): boolean;
  serialize(): string;
  /** 第一次读入之前为 0。0 before the first load. */
  version(): number;
  eol(): CodeEol;
  /** 每次内容变化（含换行切换）后调用。Called after every content change, line break switches included. */
  onDidChangeContent(listener: () => void): CodeTextDisposable;
  dispose(): void;
}

/** 加载 Monaco 并创建文本模型。Loads Monaco and creates a text model. */
export async function loadCodeTextModel(): Promise<CodeTextModel> {
  const { CodeTextModelImpl } = await import("./monaco/text-model-impl");
  return new CodeTextModelImpl();
}
