import type { JSONContent } from "@tiptap/core";
import type { Node as ProseMirrorNode, Schema } from "@tiptap/pm/model";

import { splitFrontMatter } from "../markdown/front-matter";
import { parseEditorMarkdown } from "./markdown-parser";
import { normalizeDocumentWithOrigins } from "./normalize-editor-document";
import {
  MarkdownSourceMapError,
  mapSourceBlocks,
  normalizeSourceLineEndings,
  pairSourceBlocks,
  type SourceBlock,
} from "./source-map";

// 载入（设计 5.1）：分出 front matter，记录块与列表项的位置，解析并规范化。
// Loading (design section 5.1): split off the front matter, record the positions of blocks and list items, parse and
// normalize.

/**
 * 基线保留原文及其换行；`blocks[i]` 为 `doc` 第 i 个顶层子节点在原文中的区间。
 * The baseline preserves the original text and line endings; `blocks[i]` is the range of `doc`'s i-th top-level child
 * in that original source.
 */
export type MarkdownBaseline = {
  readonly source: string;
  readonly doc: ProseMirrorNode;
  readonly blocks: readonly SourceBlock[];
};

export type LoadedMarkdown = {
  /** 规范化后的文档，`baseline.doc` 就是它。The normalized document; `baseline.doc` is this node. */
  readonly doc: ProseMirrorNode;
  readonly baseline: MarkdownBaseline;
};

/**
 * 载入 Markdown；解析时换行统一为 LF，基线保留原文及其位置。`schema` 为编辑器的 schema（Node.eq 按节点类型对象对比，
 * 基线必须与编辑器使用同一个 schema）。
 * 抛出 FrontMatterError（front matter 不是有效的 YAML）、MarkdownSourceMapError，或文档不符合 schema 时的 RangeError。
 * Loads Markdown with LF-normalized parser input and source offsets in the original text; `schema` is the editor's
 * schema (Node.eq compares node types by identity, so the baseline must use the editor's schema).
 * Throws FrontMatterError (invalid YAML front matter), MarkdownSourceMapError, or RangeError when the document does
 * not fit the schema.
 */
export function loadMarkdown(source: string, schema: Schema): LoadedMarkdown {
  const normalizedSource = normalizeSourceLineEndings(source);
  const { prefix, text, body } = splitFrontMatter(normalizedSource.text);
  const sourcePrefixLength = normalizedSource.sourceOffsets[prefix.length];
  const sourceBody = source.slice(sourcePrefixLength);
  const tokenBlocks = mapSourceBlocks(sourceBody, sourcePrefixLength);
  const parsed = parseEditorMarkdown(body).content ?? [];
  const paired = pairSourceBlocks(parsed, tokenBlocks);
  const content: JSONContent[] = [...parsed];
  const sources: (SourceBlock | null)[] = [...paired];
  if (text !== null) {
    content.unshift({ type: "frontMatter", content: text ? [{ type: "text", text }] : undefined });
    // front matter 的区间为整个前缀，与其他块一样去掉末尾的换行。The front matter's range is the whole prefix without
    // its trailing newline, like every other block.
    const prefixEnd = prefix.endsWith("\n") ? prefix.length - 1 : prefix.length;
    sources.unshift({
      range: { from: 0, to: normalizedSource.sourceOffsets[prefixEnd] },
      items: null,
    });
  }
  const normalized = normalizeDocumentWithOrigins({ type: "doc", content });
  let end = 0;
  const blocks = normalized.origins.map((origin, index): SourceBlock => {
    if (origin < 0) {
      // 规范化加入的空 paragraph 在原文中没有文字。The empty paragraph added by normalization has no source text.
      return { range: { from: end, to: end }, items: null };
    }
    const block = sources[origin];
    if (!block) {
      throw new MarkdownSourceMapError(
        `The ${normalized.doc.content![index].type} block has no position in the source.`,
      );
    }
    end = block.range.to;
    return block;
  });
  const doc = schema.nodeFromJSON(normalized.doc);
  // 与编辑器的 enableContentCheck 相同：内容不符合 schema 时抛出。Like the editor's enableContentCheck: content that
  // does not fit the schema throws.
  doc.check();
  return { doc, baseline: { source, doc, blocks } };
}
