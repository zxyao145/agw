import type { AnyExtension, JSONContent } from "@tiptap/core";
import { MarkdownManager } from "@tiptap/markdown";
import { Marked, type marked as globalMarked, type Token } from "marked";
import markedFootnote from "marked-footnote";

import { INLINE_WRITER_EXTENSIONS } from "../markdown/markdown-extensions";
import { registerMarkdownReader, type SpellingBudget } from "../markdown/markdown-inline";
import { definitionExtension, editTokenizers } from "../markdown/parse-markdown";
import { editorExtensions } from "./editor-extensions";

// marked-footnote 的状态保存在每个实例中，每次解析与位置计算都新建一组 Marked、MarkdownManager 与 markedFootnote()。
// marked-footnote keeps its state per instance, so every parse and every source-map lex creates new Marked,
// MarkdownManager and markedFootnote() instances.

function createEditorParser(extensions: AnyExtension[] = editorExtensions): {
  marked: Marked;
  manager: MarkdownManager;
} {
  const marked = new Marked();
  marked.use({ tokenizer: editTokenizers, extensions: [definitionExtension] });
  // 选项的类型是全局 marked；MarkdownManager 只使用 Marked 实例同样提供的 Lexer、defaults 与 use。
  // The option is typed as the global marked; MarkdownManager uses only Lexer, defaults and use.
  const manager = new MarkdownManager({
    marked: marked as unknown as typeof globalMarked,
    extensions,
  });
  // 在 MarkdownManager 之后注册，脚注 tokenizer 先执行。Registered last so the footnote tokenizer runs first.
  marked.use(markedFootnote());
  return { marked, manager };
}

/** 用 Edit 的配置解析 Markdown 正文。Parses a Markdown body with Edit's configuration. */
export function parseEditorMarkdown(body: string): JSONContent {
  return createEditorParser().manager.parse(body);
}

registerMarkdownReader(parseEditorMarkdown);

/** 用与解析相同的配置取得顶层 token。Lexes the top-level tokens with the same configuration as parsing. */
export function lexEditorMarkdown(body: string): Token[] {
  const { marked } = createEditorParser();
  return new marked.Lexer(marked.defaults).lex(body);
}

/**
 * 一次保存输出修改块用的 MarkdownManager：输出行内内容的扩展共用 `budget`。它的 Marked 实例只用于注册，不解析。
 * The MarkdownManager that writes the modified blocks of one save: the extensions that write inline content share
 * `budget`. Its Marked instance only receives registrations and never parses.
 */
export function createEditorRenderer(budget: SpellingBudget): MarkdownManager {
  const extensions = editorExtensions.map((extension) =>
    INLINE_WRITER_EXTENSIONS.has(extension.name)
      ? extension.configure({ spellingBudget: budget })
      : extension,
  );
  return createEditorParser(extensions).manager;
}
