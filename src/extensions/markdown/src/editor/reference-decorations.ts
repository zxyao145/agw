import { Extension } from "@tiptap/core";
import type { Node as ProseMirrorNode } from "@tiptap/pm/model";
import { Plugin, PluginKey } from "@tiptap/pm/state";
import { Decoration, DecorationSet } from "@tiptap/pm/view";

import { changedBlockRanges } from "./changed-ranges";
import {
  markdownDefinitionsKey,
  resolveReference,
  type MarkdownDefinitions,
} from "./markdown-definitions";

// 引用式链接与图片的显示（设计 5.4）：定义被删除的引用显示为 "Missing link definition" 样式；引用式图片的地址由当前
// 定义解析，放在 node 装饰中交给图片的 node view。定义变化时全部重新计算，否则只重新计算改动过的块。
// Display of reference links and images (design section 5.4): a reference whose definition is gone shows the
// "Missing link definition" style; a reference image's address is resolved from the current definitions and handed
// to the image node view in a node decoration. A definition change recomputes everything; otherwise only changed
// blocks are recomputed.

export const MISSING_DEFINITION = "Missing link definition";
const MISSING_ATTRIBUTES = { class: "missing-link-definition", title: MISSING_DEFINITION };

type ReferenceSpec = { referenceSource: string | null };

/** 引用式图片解析后的地址；没有定义时为 null。A reference image's resolved address; null without a definition. */
export function referenceSource(decorations: readonly Decoration[]): string | null {
  const decoration = decorations.find((candidate) => "referenceSource" in candidate.spec);
  if (!decoration) throw new Error("The reference image has no resolved address.");
  return (decoration.spec as ReferenceSpec).referenceSource;
}

function collect(
  doc: ProseMirrorNode,
  from: number,
  to: number,
  definitions: MarkdownDefinitions,
  decorations: Decoration[],
): void {
  doc.nodesBetween(from, to, (node, pos) => {
    if (node.type.name === "image" && node.attrs.reference !== null) {
      const resolved = resolveReference(definitions, String(node.attrs.reference));
      decorations.push(
        Decoration.node(pos, pos + node.nodeSize, resolved ? {} : MISSING_ATTRIBUTES, {
          referenceSource: resolved?.href ?? null,
        } satisfies ReferenceSpec),
      );
    } else if (node.isText) {
      const link = node.marks.find(
        (mark) => mark.type.name === "link" && mark.attrs.reference !== null,
      );
      if (link && !resolveReference(definitions, String(link.attrs.reference))) {
        decorations.push(Decoration.inline(pos, pos + node.nodeSize, MISSING_ATTRIBUTES));
      }
    }
    return true;
  });
}

function build(doc: ProseMirrorNode, definitions: MarkdownDefinitions): DecorationSet {
  const decorations: Decoration[] = [];
  collect(doc, 0, doc.content.size, definitions, decorations);
  return DecorationSet.create(doc, decorations);
}

const referenceDecorationsKey = new PluginKey<DecorationSet>("referenceDecorations");

export const ReferenceDecorations = Extension.create({
  name: "referenceDecorations",
  // 插件状态读取定义插件的状态，必须排在它之后（Tiptap 按优先级从高到低排列插件）。
  // The plugin state reads the definitions plugin's state, so it must come after it (Tiptap orders plugins by
  // descending priority).
  priority: 50,
  addProseMirrorPlugins() {
    return [
      new Plugin<DecorationSet>({
        key: referenceDecorationsKey,
        state: {
          init: (_config, state) => build(state.doc, markdownDefinitionsKey.getState(state)!),
          apply: (tr, decorations, oldState, newState) => {
            const definitions = markdownDefinitionsKey.getState(newState)!;
            if (definitions !== markdownDefinitionsKey.getState(oldState)) {
              return build(newState.doc, definitions);
            }
            if (!tr.docChanged) return decorations;
            let next = decorations.map(tr.mapping, tr.doc);
            for (const range of changedBlockRanges([tr], tr.doc)) {
              next = next.remove(next.find(range.from, range.to));
              const added: Decoration[] = [];
              collect(tr.doc, range.from, range.to, definitions, added);
              next = next.add(tr.doc, added);
            }
            return next;
          },
        },
        props: { decorations: (state) => referenceDecorationsKey.getState(state) },
      }),
    ];
  },
});
