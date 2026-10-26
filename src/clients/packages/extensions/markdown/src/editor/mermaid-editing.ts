import { Extension } from "@tiptap/core";
import type { Node as ProseMirrorNode, ResolvedPos } from "@tiptap/pm/model";
import {
  NodeSelection,
  Plugin,
  PluginKey,
  Selection,
  TextSelection,
  type EditorState,
  type Transaction,
} from "@tiptap/pm/state";
import { Decoration, DecorationSet, type EditorView } from "@tiptap/pm/view";

import { isMermaidLanguage } from "../shared/mermaid-render";

// Mermaid 代码块的视图与编辑状态（Mermaid 设计第 2、3 节）。
// - 默认视图显示图表；选区一端所在的 Mermaid 代码块带编辑状态的 node 装饰，它的 node view 同时显示源码。源码隐藏时浏览器
//   的光标移动会跳过它，方向键由这里放进代码块。
// - "Diagram only" 视图只显示图表。视图保存在插件状态中（随文档映射的 node 装饰），不写入 Markdown，载入文件时回到默认
//   视图。这个视图中的代码块像图片一样整体选中：选区进入它时改为选中整个代码块，方向键离开选中的代码块，Backspace 与
//   Delete 遇到它时先选中它，不把相邻的文字合并进隐藏的源码。
// The views and editing state of Mermaid code blocks (Mermaid design sections 2 and 3).
// - The default view shows the diagram; the Mermaid code block holding either end of the selection gets an editing node
//   decoration, and its node view shows the source as well. Native caret motion skips hidden source, so the arrow keys
//   that enter such a block are handled here.
// - The "Diagram only" view shows only the diagram. Views live in the plugin state (node decorations mapped with the
//   document), are not written to the Markdown, and return to the default view when a file loads. A block in this view
//   is selected as a whole like an image: a selection entering it selects the whole block, arrow keys leave the selected
//   block, and Backspace and Delete select it first, so adjacent text is not merged into the hidden source.

type EditingSpec = { mermaidEditing: true };
type ViewSpec = { mermaidView: "diagram" };
type ViewMeta = { pos: number; diagramOnly: boolean };

type Arrow = { direction: "up" | "down" | "left" | "right"; forward: boolean };

const ARROWS: Record<string, Arrow> = {
  ArrowUp: { direction: "up", forward: false },
  ArrowDown: { direction: "down", forward: true },
  ArrowLeft: { direction: "left", forward: false },
  ArrowRight: { direction: "right", forward: true },
};

const mermaidViewKey = new PluginKey<DecorationSet>("mermaidView");

function isMermaidBlock(node: ProseMirrorNode): boolean {
  return node.type.name === "codeBlock" && isMermaidLanguage(node.attrs.language as string | null);
}

function isViewSpec(spec: Partial<ViewSpec>): boolean {
  return spec.mermaidView === "diagram";
}

/** `pos` 处的 Mermaid 代码块是否为 "Diagram only" 视图。Whether the Mermaid code block at `pos` is in the Diagram only view. */
function isDiagramOnlyAt(state: EditorState, pos: number): boolean {
  return mermaidViewKey
    .getState(state)!
    .find(pos, pos, isViewSpec)
    .some((decoration) => decoration.from === pos);
}

/** node view 的装饰是否表示它处于编辑状态。Whether a node view's decorations mark it as being edited. */
export function isMermaidEditing(decorations: readonly Decoration[]): boolean {
  return decorations.some(
    (decoration) => (decoration.spec as Partial<EditingSpec>).mermaidEditing === true,
  );
}

/** node view 的装饰是否表示 "Diagram only" 视图。Whether a node view's decorations mark the Diagram only view. */
export function isDiagramOnly(decorations: readonly Decoration[]): boolean {
  return decorations.some((decoration) => isViewSpec(decoration.spec as Partial<ViewSpec>));
}

/**
 * 切换 `pos` 处 Mermaid 代码块的视图，不修改文档。
 * Switches the view of the Mermaid code block at `pos` without changing the document.
 */
export function setMermaidDiagramOnly(view: EditorView, pos: number, diagramOnly: boolean): void {
  view.dispatch(view.state.tr.setMeta(mermaidViewKey, { pos, diagramOnly } satisfies ViewMeta));
}

function applyViews(tr: Transaction, views: DecorationSet): DecorationSet {
  const mapped = views.map(tr.mapping, tr.doc);
  const meta = tr.getMeta(mermaidViewKey) as ViewMeta | undefined;
  if (meta === undefined) return mapped;
  const node = tr.doc.nodeAt(meta.pos);
  if (!node || !isMermaidBlock(node)) throw new Error(`No Mermaid code block at ${meta.pos}.`);
  const others = mapped.remove(
    mapped
      .find(meta.pos, meta.pos, isViewSpec)
      .filter((decoration) => decoration.from === meta.pos),
  );
  if (!meta.diagramOnly) return others;
  return others.add(tr.doc, [
    Decoration.node(meta.pos, meta.pos + node.nodeSize, {}, {
      mermaidView: "diagram",
    } satisfies ViewSpec),
  ]);
}

function decorations(state: EditorState): DecorationSet {
  const views = mermaidViewKey.getState(state)!;
  const { $from, $to } = state.selection;
  const editing: Decoration[] = [];
  for (const $position of $from.sameParent($to) ? [$from] : [$from, $to]) {
    if (!isMermaidBlock($position.parent)) continue;
    const start = $position.before();
    editing.push(
      Decoration.node(start, start + $position.parent.nodeSize, {}, {
        mermaidEditing: true,
      } satisfies EditingSpec),
    );
  }
  return editing.length > 0 ? views.add(state.doc, editing) : views;
}

/**
 * 选区一端在 "Diagram only" 视图的代码块中时，改为选中整个代码块。
 * When either end of the selection is in a block in the Diagram only view, selects the whole block instead.
 */
function selectDiagramOnlyBlock(state: EditorState): Transaction | null {
  const { selection } = state;
  if (selection instanceof NodeSelection) return null;
  for (const $position of [selection.$from, selection.$to]) {
    if (!isMermaidBlock($position.parent)) continue;
    const start = $position.before();
    if (isDiagramOnlyAt(state, start)) {
      return state.tr.setSelection(NodeSelection.create(state.doc, start));
    }
  }
  return null;
}

/**
 * 光标在 textblock 边缘、按方向键离开它，并且下一个文字位置在 Mermaid 代码块中时，把光标放到那里：向下、向右为源码开头，
 * 向上、向左为源码末尾（"Diagram only" 视图的代码块随后整体选中）。其他情况交给 ProseMirror 与浏览器。
 * When the cursor leaves a textblock edge with an arrow key and the next text position is in a Mermaid code block,
 * puts the cursor there: the start of the source going down or right, its end going up or left (a block in the Diagram
 * only view then gets selected as a whole). Everything else is left to ProseMirror and the browser.
 */
function enterMermaidBlock(view: EditorView, arrow: Arrow): boolean {
  const { selection } = view.state;
  if (!(selection instanceof TextSelection) || !selection.empty) return false;
  if (!view.endOfTextblock(arrow.direction)) return false;
  const { $head } = selection;
  const boundary = view.state.doc.resolve(arrow.forward ? $head.after() : $head.before());
  const target = Selection.findFrom(boundary, arrow.forward ? 1 : -1, true);
  if (!target || !isMermaidBlock(target.$head.parent)) return false;
  view.dispatch(view.state.tr.setSelection(target).scrollIntoView());
  return true;
}

/**
 * 整体选中的 "Diagram only" 代码块：方向键把光标放到之后（向下、向右）或之前（向上、向左）的文字位置。
 * A selected block in the Diagram only view: arrow keys put the cursor at the next text position after it (down,
 * right) or before it (up, left).
 */
function leaveSelectedDiagram(view: EditorView, arrow: Arrow): boolean {
  const { selection, doc } = view.state;
  if (!(selection instanceof NodeSelection) || !isMermaidBlock(selection.node)) return false;
  if (!isDiagramOnlyAt(view.state, selection.from)) return false;
  const target = arrow.forward
    ? Selection.findFrom(doc.resolve(selection.to), 1, true)
    : Selection.findFrom(doc.resolve(selection.from), -1, true);
  if (!target) return false;
  view.dispatch(view.state.tr.setSelection(target).scrollIntoView());
  return true;
}

/** 与 prosemirror-commands 的 findCutBefore 相同。The same as prosemirror-commands' findCutBefore. */
function cutBefore($position: ResolvedPos): ResolvedPos | null {
  if ($position.parent.type.spec.isolating) return null;
  for (let depth = $position.depth - 1; depth >= 0; depth -= 1) {
    if ($position.index(depth) > 0) return $position.doc.resolve($position.before(depth + 1));
    if ($position.node(depth).type.spec.isolating) break;
  }
  return null;
}

/** 与 prosemirror-commands 的 findCutAfter 相同。The same as prosemirror-commands' findCutAfter. */
function cutAfter($position: ResolvedPos): ResolvedPos | null {
  if ($position.parent.type.spec.isolating) return null;
  for (let depth = $position.depth - 1; depth >= 0; depth -= 1) {
    const parent = $position.node(depth);
    if ($position.index(depth) < parent.childCount - 1) {
      return $position.doc.resolve($position.after(depth + 1));
    }
    if (parent.type.spec.isolating) break;
  }
  return null;
}

/**
 * Backspace 在 textblock 开头、Delete 在 textblock 末尾，并且另一侧紧邻 "Diagram only" 代码块时，选中这个代码块。
 * 与代码块同级的空 textblock 先删除，与 ProseMirror 在图片等节点旁边的处理相同。ProseMirror 自己的合并会改动代码块的边界，
 * 视图的 node 装饰随之失效，因此这里不交给它。
 * Backspace at a textblock start or Delete at its end, next to a block in the Diagram only view, selects that block. An
 * empty textblock beside the block at the same level is deleted first, as ProseMirror does next to images. ProseMirror's
 * own joining changes the block's boundaries, which drops the view's node decoration, so it is not used here.
 */
function selectAdjacentDiagram(view: EditorView, backward: boolean): boolean {
  const { state } = view;
  const { selection } = state;
  if (!(selection instanceof TextSelection) || !selection.empty) return false;
  const { $head } = selection;
  if (!view.endOfTextblock(backward ? "backward" : "forward")) return false;
  const $cut = backward ? cutBefore($head) : cutAfter($head);
  const block = backward ? $cut?.nodeBefore : $cut?.nodeAfter;
  if (!$cut || !block || !isMermaidBlock(block)) return false;
  const start = backward ? $cut.pos - block.nodeSize : $cut.pos;
  if (!isDiagramOnlyAt(state, start)) return false;
  const tr = state.tr;
  if ($head.parent.content.size === 0 && $head.depth === $cut.depth + 1) {
    tr.delete($head.before(), $head.after());
  }
  view.dispatch(
    tr.setSelection(NodeSelection.create(tr.doc, tr.mapping.map(start))).scrollIntoView(),
  );
  return true;
}

function handleKeyDown(view: EditorView, event: KeyboardEvent): boolean {
  if (event.shiftKey || event.altKey || event.ctrlKey || event.metaKey) return false;
  const arrow = ARROWS[event.key];
  if (arrow) return leaveSelectedDiagram(view, arrow) || enterMermaidBlock(view, arrow);
  if (event.key === "Backspace") return selectAdjacentDiagram(view, true);
  if (event.key === "Delete") return selectAdjacentDiagram(view, false);
  return false;
}

export const MermaidEditing = Extension.create({
  name: "mermaidEditing",
  addProseMirrorPlugins() {
    return [
      new Plugin<DecorationSet>({
        key: mermaidViewKey,
        state: { init: () => DecorationSet.empty, apply: applyViews },
        props: { decorations, handleKeyDown },
        appendTransaction: (_transactions, _oldState, state) => selectDiagramOnlyBlock(state),
      }),
    ];
  },
});
