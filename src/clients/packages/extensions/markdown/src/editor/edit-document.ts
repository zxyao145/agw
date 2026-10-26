import type { DocumentSession, ExtensionApi } from "@agw/extension-sdk";
import { Editor } from "@tiptap/core";
import type { Node as ProseMirrorNode } from "@tiptap/pm/model";
import { EditorState } from "@tiptap/pm/state";

import { createEditExtensions } from "./edit-extensions";
import { EditUi } from "./edit-ui";
import { loadMarkdown, type LoadedMarkdown, type MarkdownBaseline } from "./markdown-loader";
import { MarkdownRoundTripError, writeMarkdown } from "./markdown-writer";
import { SlashMenuStore } from "./slash-command";

// Mixed 打开的文档（设计 5.7 "文档会话的接入"）：Tiptap 编辑器与它的文档会话。快照为 `editor.state.doc`，未修改的子树
// 共用节点对象，`matches` 用 `Node.eq` 对比；载入执行 5.1 节并清空撤销历史；序列化为 5.6 节的写回。
// The document Mixed opens (design section 5.7, session wiring): the Tiptap editor and its document session. Snapshots
// are `editor.state.doc`, whose unchanged subtrees share node objects, and `matches` compares with `Node.eq`; loading
// runs section 5.1 and clears the undo history; serializing is the write-back of section 5.6.

type DocumentFile = { uri: string; name: string };

/** 写回失败时界面显示的原因（设计第 571 行）。The reason shown when write-back fails (design line 571). */
const ROUND_TRIP_MESSAGE = "the document can't be written as Markdown without changing it.";

/**
 * 编辑器中的基线：编辑器载入后可能在末尾补了空 paragraph（TrailingNode），它们在原文中没有文字，位置为最后一个块的末尾。
 * The baseline in the editor: after loading, the editor may have appended empty paragraphs (TrailingNode); they have
 * no source text and sit at the end of the last block.
 */
function editorBaseline(loaded: LoadedMarkdown, doc: ProseMirrorNode): MarkdownBaseline {
  const count = loaded.doc.childCount;
  if (doc.childCount < count) throw new Error("The editor dropped blocks of the loaded document.");
  for (let index = 0; index < count; index += 1) {
    if (!doc.child(index).eq(loaded.doc.child(index))) {
      throw new Error("The editor changed the loaded document.");
    }
  }
  const { blocks } = loaded.baseline;
  const end = blocks.at(-1)!.range.to;
  const appended = [];
  for (let index = count; index < doc.childCount; index += 1) {
    const child = doc.child(index);
    if (child.type.name !== "paragraph" || child.content.size > 0) {
      throw new Error(`The editor appended a ${child.type.name} block to the loaded document.`);
    }
    appended.push({ range: { from: end, to: end }, items: null });
  }
  return { source: loaded.baseline.source, doc, blocks: [...blocks, ...appended] };
}

export class EditDocument {
  public readonly editor: Editor;
  public readonly session: DocumentSession;
  public readonly ui = new EditUi();
  public readonly slashMenu = new SlashMenuStore();
  /** 文档快照到它在磁盘上的 Markdown 基线：载入的文档与每个序列化过的快照。Document snapshots to their Markdown
   *  baseline on disk: the loaded document and every serialized snapshot. */
  private readonly baselines = new WeakMap<ProseMirrorNode, MarkdownBaseline>();
  private edits = 0;

  public constructor(api: ExtensionApi, file: DocumentFile) {
    this.editor = new Editor({
      element: document.createElement("div"),
      extensions: createEditExtensions({
        api,
        ui: this.ui,
        slashMenu: this.slashMenu,
        fileUri: () => this.session.file.uri,
      }),
      // 共用的 Markdown 排版，document 密度。The shared Markdown typography in document density.
      editorProps: { attributes: { class: "agw-prose", "data-density": "document" } },
      enableContentCheck: true,
      onContentError: ({ error }) => {
        throw error;
      },
    });
    let doc = this.editor.state.doc;
    this.editor.on("transaction", () => {
      if (this.editor.state.doc === doc) return;
      doc = this.editor.state.doc;
      this.edits += 1;
      this.session.edited();
    });
    this.session = api.documents.openText<ProseMirrorNode>({
      file,
      adapter: {
        load: (content) => this.load(content.text),
        snapshot: () => this.editor.state.doc,
        serialize: (snapshot, baseline) => this.serialize(snapshot, baseline),
        matches: (snapshot) => this.editor.state.doc.eq(snapshot),
        version: () => this.edits,
      },
    });
  }

  /** 在 `view.onShow` 中调用。Called from `view.onShow`. */
  public show(file: DocumentFile): Promise<void> {
    return this.session.show(file);
  }

  public dispose(): void {
    this.session.dispose();
    this.editor.destroy();
  }

  private load(text: string): void {
    const loaded = loadMarkdown(text.replace(/\r\n?/g, "\n"), this.editor.schema);
    this.editor.commands.setContent(loaded.doc.content, { emitUpdate: false });
    // 补节点的插件已经执行；用同一个文档新建状态，撤销历史随之清空。
    // The plugins that append nodes have run; a new state over the same document clears the undo history.
    const { state } = this.editor;
    this.editor.view.updateState(EditorState.create({ doc: state.doc, plugins: state.plugins }));
    this.baselines.set(this.editor.state.doc, editorBaseline(loaded, this.editor.state.doc));
  }

  private serialize(snapshot: ProseMirrorNode, baseline: ProseMirrorNode): string {
    const markdown = this.baselines.get(baseline);
    if (!markdown) throw new Error("The session's baseline snapshot has no Markdown baseline.");
    let result: ReturnType<typeof writeMarkdown>;
    try {
      result = writeMarkdown(markdown, snapshot);
    } catch (error) {
      if (!(error instanceof MarkdownRoundTripError)) throw error;
      // 控制台记录不一致的块路径与类型，通知中显示设计给出的原因。The console records the differing block path and
      // types; the notification shows the design's reason.
      console.error(error.message, error.failure);
      throw new Error(ROUND_TRIP_MESSAGE, { cause: error });
    }
    // 与基线相同（例如按其他编码保存）时写出基线的原文。Equal to the baseline (for example when saving with another
    // encoding): the baseline's source is written.
    const written = result ?? { text: markdown.source, baseline: { ...markdown, doc: snapshot } };
    this.baselines.set(snapshot, written.baseline);
    return written.text;
  }
}
