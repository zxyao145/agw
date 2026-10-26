import type { JSONContent } from "@tiptap/core";
import type { Node as ProseMirrorNode, Schema } from "@tiptap/pm/model";

import { normalizeEditorDocument } from "./normalize-editor-document";

// 内容对比（设计 5.3、5.6）：两边都经过 normalizeEditorDocument 并去掉写法属性后，用 schema.nodeFromJSON 生成节点，
// 再用 Node.eq 对比。Fragment.fromArray 会合并相邻的、标记相同的文字节点。
// Content comparison (design sections 5.3 and 5.6): both sides go through normalizeEditorDocument, drop their syntax
// attributes, become nodes through schema.nodeFromJSON and are compared with Node.eq. Fragment.fromArray merges
// adjacent text nodes with equal marks.

/** 写法属性：只记录原文的写法，不决定文档的含义。Syntax attributes: they record how the source was written and do not
 *  change what the document means. */
export const SYNTAX_ATTRIBUTES: Readonly<Record<string, readonly string[]>> = {
  codeBlock: ["fenced", "fence"],
  heading: ["style"],
  bulletList: ["marker"],
  orderedList: ["delimiter"],
  link: ["reference", "referenceKind"],
  image: ["reference", "referenceKind"],
};

function withoutSyntaxAttributes<T extends { type?: string; attrs?: Record<string, unknown> }>(
  item: T,
): T {
  const names = SYNTAX_ATTRIBUTES[item.type ?? ""];
  if (!names || !item.attrs) return item;
  const attrs = { ...item.attrs };
  for (const name of names) delete attrs[name];
  return { ...item, attrs };
}

function stripSyntax(node: JSONContent): JSONContent {
  const stripped = withoutSyntaxAttributes(node);
  return {
    ...stripped,
    marks: node.marks?.map((mark) => withoutSyntaxAttributes(mark)),
    content: node.content?.map(stripSyntax),
  };
}

/** 用于内容对比的文档。The document used for content comparison. */
export function comparableDocument(doc: JSONContent, schema: Schema): ProseMirrorNode {
  return schema.nodeFromJSON(stripSyntax(normalizeEditorDocument(doc)));
}

export type DocumentDifference = {
  /** 从文档开始的子节点序号。Child indices from the document root. */
  readonly path: readonly number[];
  readonly expected: string;
  readonly actual: string;
};

function describe(node: ProseMirrorNode | undefined): string {
  if (!node) return "nothing";
  return node.isText ? `text ${JSON.stringify(node.text)}` : node.type.name;
}

/**
 * 第一个不相同的节点的路径与类型；两个文档相同时返回 null。
 * The path and types of the first differing node; null when both documents are equal.
 */
export function findDifference(
  expected: ProseMirrorNode,
  actual: ProseMirrorNode,
  path: number[] = [],
): DocumentDifference | null {
  if (expected.eq(actual)) return null;
  if (!expected.sameMarkup(actual) || expected.isText || actual.isText) {
    return { path, expected: describe(expected), actual: describe(actual) };
  }
  const count = Math.max(expected.childCount, actual.childCount);
  for (let index = 0; index < count; index++) {
    const left = expected.maybeChild(index);
    const right = actual.maybeChild(index);
    if (left && right) {
      const difference = findDifference(left, right, [...path, index]);
      if (difference) return difference;
    } else {
      return {
        path: [...path, index],
        expected: describe(left ?? undefined),
        actual: describe(right ?? undefined),
      };
    }
  }
  return { path, expected: describe(expected), actual: describe(actual) };
}
