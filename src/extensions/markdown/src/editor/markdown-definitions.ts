import { Extension, type JSONContent } from "@tiptap/core";
import type { Node as ProseMirrorNode } from "@tiptap/pm/model";
import { Plugin, PluginKey, type EditorState, type Transaction } from "@tiptap/pm/state";
import { ReplaceAroundStep, ReplaceStep } from "@tiptap/pm/transform";

import {
  createFootnoteLabelReader,
  normalizeLinkLabel,
  parseLinkDefinition,
  type LinkDefinition,
} from "../markdown/markdown-syntax";

// 文档中的链接定义与脚注定义（设计 5.4 第 4、5 步）。规范化在载入与内容对比时使用它们；编辑时由插件保持：
// 链接的显示地址取自当前的定义，没有定义的脚注引用改为文字。
// The document's link and footnote definitions (design section 5.4 steps 4 and 5). Normalization uses them at load
// and for comparison; while editing a plugin keeps them: a link's displayed destination comes from the current
// definitions and a footnote reference without a definition becomes text.

export type MarkdownDefinitions = {
  /** 标签（按 marked 的规则）到地址与标题，同名时第一个有效。Label (by marked's rule) to destination and title; the
   *  first of the same name wins. */
  readonly links: ReadonlyMap<string, LinkDefinition>;
  /** 脚注定义的标签，按原样对比。Footnote definition labels, compared as written. */
  readonly footnotes: ReadonlySet<string>;
};

type RawBlockText = { kind: string; text: string };

function definitionsFrom(blocks: readonly RawBlockText[]): MarkdownDefinitions {
  const links = new Map<string, LinkDefinition>();
  const footnotes = new Set<string>();
  const readFootnoteLabel = createFootnoteLabelReader();
  for (const { kind, text } of blocks) {
    if (kind === "definition") {
      const definition = parseLinkDefinition(text);
      if (definition && !links.has(definition.label)) {
        links.set(definition.label, { href: definition.href, title: definition.title });
      }
    } else if (kind === "footnote") {
      const label = readFootnoteLabel(text);
      if (label !== null) footnotes.add(label);
    }
  }
  return { links, footnotes };
}

function collectJsonRawBlocks(node: JSONContent, blocks: RawBlockText[]): void {
  if (node.type === "rawBlock") {
    blocks.push({
      kind: String(node.attrs?.kind ?? "html"),
      text: (node.content ?? []).map((child) => child.text ?? "").join(""),
    });
    return;
  }
  for (const child of node.content ?? []) {
    if (child.type !== "text") collectJsonRawBlocks(child, blocks);
  }
}

/** Tiptap JSON 文档中的定义。The definitions of a Tiptap JSON document. */
export function jsonDefinitions(doc: JSONContent): MarkdownDefinitions {
  const blocks: RawBlockText[] = [];
  collectJsonRawBlocks(doc, blocks);
  return definitionsFrom(blocks);
}

/** ProseMirror 文档中的定义；只遍历块，不进入行内内容。The definitions of a ProseMirror document; visits blocks only. */
export function documentDefinitions(doc: ProseMirrorNode): MarkdownDefinitions {
  const blocks: RawBlockText[] = [];
  doc.descendants((node) => {
    if (node.type.name === "rawBlock") {
      blocks.push({ kind: String(node.attrs.kind), text: node.textContent });
      return false;
    }
    return !node.isTextblock && !node.isAtom;
  });
  return definitionsFrom(blocks);
}

/** 引用式链接按当前定义解析的地址与标题；没有定义时为 null。The destination and title of a reference link under the
 *  current definitions; null when it has no definition. */
export function resolveReference(
  definitions: MarkdownDefinitions,
  reference: string,
): LinkDefinition | null {
  return definitions.links.get(normalizeLinkLabel(reference)) ?? null;
}

function definitionsEqual(a: MarkdownDefinitions, b: MarkdownDefinitions): boolean {
  if (a.links.size !== b.links.size || a.footnotes.size !== b.footnotes.size) return false;
  for (const [label, definition] of a.links) {
    const other = b.links.get(label);
    if (!other || other.href !== definition.href || other.title !== definition.title) return false;
  }
  for (const label of a.footnotes) if (!b.footnotes.has(label)) return false;
  return true;
}

/**
 * 标签不在脚注定义中的脚注引用的位置，从后往前排列。
 * Positions of footnote references whose label has no footnote definition, last first.
 */
export function unresolvedFootnoteReferences(
  doc: ProseMirrorNode,
  footnotes: ReadonlySet<string>,
): { pos: number; node: ProseMirrorNode }[] {
  const found: { pos: number; node: ProseMirrorNode }[] = [];
  doc.descendants((node, pos) => {
    if (node.type.name === "footnoteReference" && !footnotes.has(String(node.attrs.label))) {
      found.push({ pos, node });
    }
    return !node.isAtom;
  });
  return found.reverse();
}

/**
 * 把没有定义的脚注引用改为文字 `[^label]`（保留标记）；没有这样的引用时返回 null。
 * Replaces footnote references without a definition by the text `[^label]` (marks kept); null when there are none.
 */
export function replaceUnresolvedFootnoteReferences(
  state: EditorState,
  footnotes: ReadonlySet<string>,
): Transaction | null {
  const references = unresolvedFootnoteReferences(state.doc, footnotes);
  if (!references.length) return null;
  const tr = state.tr;
  for (const { pos, node } of references) {
    tr.replaceWith(
      pos,
      pos + node.nodeSize,
      state.schema.text(`[^${String(node.attrs.label)}]`, node.marks),
    );
  }
  return tr;
}

function insertsFootnoteReference(tr: Transaction): boolean {
  return tr.steps.some((step) => {
    if (!(step instanceof ReplaceStep) && !(step instanceof ReplaceAroundStep)) return false;
    let found = false;
    step.slice.content.descendants((node) => {
      if (node.type.name === "footnoteReference") found = true;
      return !found;
    });
    return found;
  });
}

export const markdownDefinitionsKey = new PluginKey<MarkdownDefinitions>("markdownDefinitions");

/**
 * 插件状态为当前文档的定义（界面用它显示引用式链接的地址与 "Missing link definition" 样式）；文档变化后，
 * 没有定义的脚注引用改为文字。链接的 href 不写入标记，引用它的 paragraph 不会因此成为修改块。
 * The plugin state holds the current document's definitions (the UI shows reference link destinations and the
 * "Missing link definition" style from it); after a document change, footnote references without a definition
 * become text. Link hrefs are never written into marks, so referencing paragraphs do not become modified blocks.
 */
export const MarkdownDefinitionsExtension = Extension.create({
  name: "markdownDefinitions",
  addProseMirrorPlugins() {
    return [
      new Plugin<MarkdownDefinitions>({
        key: markdownDefinitionsKey,
        state: {
          init: (_config, state) => documentDefinitions(state.doc),
          apply: (tr, value, _oldState, newState) => {
            if (!tr.docChanged) return value;
            const next = documentDefinitions(newState.doc);
            return definitionsEqual(next, value) ? value : next;
          },
        },
        appendTransaction: (transactions, oldState, newState) => {
          if (!transactions.some((tr) => tr.docChanged)) return null;
          const footnotes = markdownDefinitionsKey.getState(newState)!.footnotes;
          const labelsChanged = markdownDefinitionsKey.getState(oldState)!.footnotes !== footnotes;
          if (!labelsChanged && !transactions.some(insertsFootnoteReference)) return null;
          return replaceUnresolvedFootnoteReferences(newState, footnotes);
        },
      }),
    ];
  },
});
