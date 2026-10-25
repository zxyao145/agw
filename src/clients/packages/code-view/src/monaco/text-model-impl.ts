import type { CodeEol, CodeTextDisposable, CodeTextFile, CodeTextModel } from "../text-model";
import { createCodeModel } from "./editor-options";
import { monaco } from "./setup";

const { EndOfLineSequence } = monaco.editor;

type CurrentModel = {
  model: monaco.editor.ITextModel;
  file: CodeTextFile;
  subscription: monaco.IDisposable;
};

/**
 * `CodeTextModel` 的实现（设计文档 5.1 节）。编辑器通过 `current` 与 `onDidChangeModel` 跟随当前文件的模型。
 * The `CodeTextModel` implementation (design section 5.1). Editors follow the current file's model through `current`
 * and `onDidChangeModel`.
 */
export class CodeTextModelImpl implements CodeTextModel {
  private currentModel: CurrentModel | null = null;
  private switching = false;
  private readonly contentListeners = new Set<() => void>();
  private readonly modelListeners = new Set<() => void>();

  /** 当前文件的模型，第一次读入之前为 null。The current file's model; null before the first load. */
  public get current(): monaco.editor.ITextModel | null {
    return this.currentModel?.model ?? null;
  }

  /** 模型所属的文件。The file the model belongs to. */
  public get file(): CodeTextFile | null {
    return this.currentModel?.file ?? null;
  }

  public load(content: { text: string; eol: CodeEol }, file: CodeTextFile): void {
    let model: monaco.editor.ITextModel;
    if (!this.switching && this.currentModel !== null && this.currentModel.file.uri === file.uri) {
      model = this.currentModel.model;
      // setValue 同时清空撤销历史。setValue also clears the undo history.
      model.setValue(content.text);
    } else {
      this.switching = false;
      const previous = this.currentModel;
      model = createCodeModel(content.text, file.name);
      this.currentModel = {
        model,
        file: { uri: file.uri, name: file.name },
        subscription: model.onDidChangeContent(() => {
          for (const listener of this.contentListeners) listener();
        }),
      };
      for (const listener of this.modelListeners) listener();
      previous?.subscription.dispose();
      previous?.model.dispose();
    }
    model.setEOL(content.eol === "\r\n" ? EndOfLineSequence.CRLF : EndOfLineSequence.LF);
  }

  public beginSwitch(): void {
    this.switching = true;
  }

  public snapshot(): number {
    return this.requireModel().getAlternativeVersionId();
  }

  public matches(snapshot: number): boolean {
    return this.requireModel().getAlternativeVersionId() === snapshot;
  }

  public serialize(): string {
    return this.requireModel().getValue();
  }

  public version(): number {
    // 第一次读入之前没有模型，也没有输入。Before the first load there is no model and no edit.
    return this.currentModel?.model.getVersionId() ?? 0;
  }

  public eol(): CodeEol {
    return this.requireModel().getEndOfLineSequence() === EndOfLineSequence.CRLF ? "\r\n" : "\n";
  }

  public onDidChangeContent(listener: () => void): CodeTextDisposable {
    this.contentListeners.add(listener);
    return { dispose: () => this.contentListeners.delete(listener) };
  }

  /** 换了文件的模型之后调用。Called after the model is replaced by another file's. */
  public onDidChangeModel(listener: () => void): CodeTextDisposable {
    this.modelListeners.add(listener);
    return { dispose: () => this.modelListeners.delete(listener) };
  }

  public dispose(): void {
    this.contentListeners.clear();
    this.modelListeners.clear();
    this.currentModel?.subscription.dispose();
    this.currentModel?.model.dispose();
    this.currentModel = null;
  }

  private requireModel(): monaco.editor.ITextModel {
    if (this.currentModel === null) throw new Error("The text model has no content yet.");
    return this.currentModel.model;
  }
}
