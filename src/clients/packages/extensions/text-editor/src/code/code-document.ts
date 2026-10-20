import type { DocumentSession, Eol, ExtensionApi } from "@agw/extension-sdk";

import { detectLanguage } from "./languages";
import { monaco } from "./monaco";

type DocumentFile = { uri: string; name: string };

type CurrentModel = {
  model: monaco.editor.ITextModel;
  file: DocumentFile;
  subscription: monaco.IDisposable;
};

const { EndOfLineSequence } = monaco.editor;

/**
 * Code 打开的文档（6.7 节）：当前文件的 Monaco 模型与它的文档会话。快照为模型的 `getAlternativeVersionId()`，撤销到基线
 * 时恢复为已保存；同一个文件重新载入时用 `setValue`，换了文件时释放旧模型、为新文件创建模型。
 * The document Code opens (section 6.7): the current file's Monaco model and its document session. Snapshots are the
 * model's `getAlternativeVersionId()`, so undoing to the baseline reads as saved; reloading the same file uses
 * `setValue`, and another file disposes the old model and creates a new one.
 */
export class CodeDocument {
  public readonly session: DocumentSession;
  private current: CurrentModel | null = null;
  // 会话换过文件、新文件还没有载入。下一次载入总是创建新模型，即使又回到模型所属的文件（例如另一个文件读取失败后）。
  // The session switched files and the new file has not loaded yet. The next load always creates a new model, even
  // when it returns to the model's file (for example after another file failed to read).
  private switching = false;
  private readonly modelListeners = new Set<() => void>();

  public constructor(api: ExtensionApi, file: DocumentFile) {
    this.session = api.documents.openText<number>({
      file,
      adapter: {
        load: (content) => this.load(content.text, content.eol),
        snapshot: () => this.requireModel().getAlternativeVersionId(),
        serialize: () => this.requireModel().getValue(),
        matches: (snapshot) => this.requireModel().getAlternativeVersionId() === snapshot,
        // 第一次载入之前没有模型，也没有输入。Before the first load there is no model and no edit.
        version: () => this.current?.model.getVersionId() ?? 0,
        eol: () =>
          this.requireModel().getEndOfLineSequence() === EndOfLineSequence.CRLF ? "\r\n" : "\n",
      },
    });
  }

  /** 当前文件的模型，第一次载入之前为 null。The current file's model; null before the first load. */
  public get model(): monaco.editor.ITextModel | null {
    return this.current?.model ?? null;
  }

  /** 模型所属的文件。The file the model belongs to. */
  public get file(): DocumentFile | null {
    return this.current?.file ?? null;
  }

  /**
   * 在 `view.onShow` 中调用：同一个文件交给会话刷新，另一个文件切换会话并在载入时创建新模型。
   * Called from `view.onShow`: the same file goes to the session for a refresh; another file switches the session and
   * gets a new model when it loads.
   */
  public show(file: DocumentFile): Promise<void> {
    if (file.uri !== this.session.file.uri) this.switching = true;
    return this.session.show(file);
  }

  /** 换了模型（打开另一个文件）后调用。Called after the model is replaced by another file's. */
  public onDidChangeModel(listener: () => void): monaco.IDisposable {
    this.modelListeners.add(listener);
    return { dispose: () => this.modelListeners.delete(listener) };
  }

  public dispose(): void {
    this.session.dispose();
    this.modelListeners.clear();
    this.current?.subscription.dispose();
    this.current?.model.dispose();
    this.current = null;
  }

  private requireModel(): monaco.editor.ITextModel {
    if (this.current === null) throw new Error("The document has no content yet.");
    return this.current.model;
  }

  private load(text: string, eol: Eol): void {
    const file = this.session.file;
    let model: monaco.editor.ITextModel;
    if (!this.switching && this.current !== null && this.current.file.uri === file.uri) {
      model = this.current.model;
      // setValue 同时清空撤销历史。setValue also clears the undo history.
      model.setValue(text);
    } else {
      this.switching = false;
      const previous = this.current;
      model = monaco.editor.createModel(
        text,
        detectLanguage(file.name, monaco.languages.getLanguages()),
      );
      this.current = {
        model,
        file: { uri: file.uri, name: file.name },
        subscription: model.onDidChangeContent(() => this.session.edited()),
      };
      for (const listener of this.modelListeners) listener();
      previous?.subscription.dispose();
      previous?.model.dispose();
    }
    model.setEOL(eol === "\r\n" ? EndOfLineSequence.CRLF : EndOfLineSequence.LF);
  }
}
