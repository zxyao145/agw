import { Extension } from "@tiptap/core";
import { Slice, type Schema } from "@tiptap/pm/model";
import { Plugin, PluginKey } from "@tiptap/pm/state";

import { parseEditorMarkdown } from "./markdown-parser";
import { normalizeEditorDocument } from "./normalize-editor-document";

// 粘贴（设计 5.7）：剪贴板只有 `text/plain`，并且光标不在代码块、`rawBlock`、`frontMatter` 中时，按 Markdown 解析后插入。
// 代码节点中由 ProseMirror 按原文插入。事务不带 paste 的 uiEvent，Tiptap 的粘贴规则不会再改写解析结果中的文字。
// Paste (design section 5.7): when the clipboard holds only `text/plain` and the cursor is not in a code block,
// `rawBlock` or `frontMatter`, the text is parsed as Markdown and inserted. ProseMirror inserts raw text in code nodes.
// The transaction carries no paste uiEvent, so Tiptap's paste rules do not rewrite text in the parsed result.

/** 用 Mixed 的解析与规范化把 Markdown 文字变成可以插入的片段。Turns Markdown text into an insertable slice with Mixed's
 *  parsing and normalization. */
export function parseMarkdownSlice(text: string, schema: Schema): Slice {
  const doc = schema.nodeFromJSON(normalizeEditorDocument(parseEditorMarkdown(text)));
  return Slice.maxOpen(doc.content);
}

export const MarkdownPaste = Extension.create({
  name: "markdownPaste",
  addProseMirrorPlugins() {
    return [
      new Plugin({
        key: new PluginKey("markdownPaste"),
        props: {
          handlePaste: (view, event) => {
            const data = event.clipboardData;
            if (!data || data.files.length > 0) return false;
            const types = [...data.types];
            if (types.some((type) => type !== "text/plain")) return false;
            const text = data.getData("text/plain");
            if (!text) return false;
            const { $from, $to } = view.state.selection;
            if ($from.parent.type.spec.code || $to.parent.type.spec.code) return false;
            const slice = parseMarkdownSlice(text.replace(/\r\n?/g, "\n"), view.state.schema);
            view.dispatch(view.state.tr.replaceSelection(slice).scrollIntoView());
            return true;
          },
        },
      }),
    ];
  },
});
