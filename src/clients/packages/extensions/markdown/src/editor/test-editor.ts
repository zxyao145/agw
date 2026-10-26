// 测试用：jsdom 提供 window、document 与 DOMParser，Tiptap Editor 本身承载文档与编辑命令。
// For tests: jsdom provides window, document and DOMParser; the Tiptap Editor itself holds the document and runs
// editing commands.
import { readdirSync, readFileSync } from "node:fs";

import { Editor } from "@tiptap/core";
import { JSDOM } from "jsdom";

import { editorExtensions } from "./editor-extensions";
import { loadMarkdown, type MarkdownBaseline } from "./markdown-loader";

const { window } = new JSDOM("");
Object.assign(globalThis, { window, document: window.document, DOMParser: window.DOMParser });

// 输入集合在包根目录的 test-inputs/ 中，格式化工具只处理 ./src，不会改动这些文件。
// The input set lives in the package's test-inputs/; the formatter handles only ./src and leaves these files alone.
const INPUT_DIRECTORY = new URL("../../test-inputs/", import.meta.url);

/** 输入集合中的每个文件。Every file of the input set. */
export function readTestInputs(): { name: string; source: string }[] {
  return readdirSync(INPUT_DIRECTORY)
    .filter((name) => name.endsWith(".md"))
    .sort()
    .map((name) => ({ name, source: readFileSync(new URL(name, INPUT_DIRECTORY), "utf8") }));
}

export type LoadedEditor = { editor: Editor; baseline: MarkdownBaseline };

/**
 * 用编辑器的 schema 载入 Markdown 并放入编辑器；基线文档为放入后的 `editor.state.doc`，它与载入的文档共用子节点。
 * 编辑器挂载在 jsdom 的元素上，插件（定义插件等）才会运行。
 * Loads Markdown with the editor's schema and puts it into the editor; the baseline document is the resulting
 * `editor.state.doc`, which shares its children with the loaded document. The editor is mounted on a jsdom element
 * so that plugins (the definitions plugin among them) run.
 */
export function openMarkdown(source: string): LoadedEditor {
  const editor = new Editor({
    element: document.createElement("div"),
    extensions: editorExtensions,
    enableContentCheck: true,
  });
  const loaded = loadMarkdown(source, editor.schema);
  editor.commands.setContent(loaded.doc.content, { emitUpdate: false });
  if (!editor.state.doc.eq(loaded.doc)) throw new Error("The editor changed the loaded document.");
  return { editor, baseline: { ...loaded.baseline, doc: editor.state.doc } };
}
