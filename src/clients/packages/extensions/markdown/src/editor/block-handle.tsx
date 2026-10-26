import type { ComputePositionConfig, Middleware } from "@floating-ui/dom";
import type { Editor } from "@tiptap/core";
import type { NestedOptions } from "@tiptap/extension-drag-handle";
import { DragHandle } from "@tiptap/extension-drag-handle-react";
import type { Node as ProseMirrorNode } from "@tiptap/pm/model";
import { type RefObject, useMemo, useRef, useState } from "react";

import { PopupButton } from "../shared/editor-chrome";
import {
  canDuplicate,
  canTurnInto,
  deleteBlock,
  duplicateBlock,
  selectBlock,
  TURN_INTO_ITEMS,
  turnInto,
} from "./block-commands";
import { firstLineRect } from "./first-line";

// 块拖动手柄（设计 5.7）：鼠标悬停时内容列的左侧显示 `⋮⋮` 手柄，垂直居中在块的第一行上（Markdown 排版设计第 5 节），
// 拖动可以移动块；点击打开块菜单：Turn into、Duplicate、Delete。顶层列表中的列表项也有手柄，手柄不随列表的缩进移动。
// The block drag handle (design section 5.7): hovering a block shows a `⋮⋮` handle left of the content column,
// vertically centered on the block's first line (Markdown typography design, section 5), that drags the block;
// clicking it opens the block menu: Turn into, Duplicate, Delete. Items of lists get handles too, and the handle does
// not follow the list's indentation.

const LIST_TYPES = new Set(["bulletList", "orderedList"]);

// 拖动的对象是块：文档的顶层子节点与顶层列表中的列表项，其他节点不作为拖动对象（扣除 1000 分即排除）。
// 不按光标靠近边缘改选父节点：列表本身不是拖动对象。
// Drag targets are blocks: the document's top-level children and the items of top-level lists; every other node is
// excluded (a 1000 deduction excludes it). Edges do not switch to the parent: the list itself is not a target.
const NESTED: NestedOptions = {
  rules: [
    {
      id: "blocks",
      evaluate: ({ node, depth, parent }) =>
        depth === 1 ||
        (depth === 2 &&
          node.type.name === "listItem" &&
          parent !== null &&
          LIST_TYPES.has(parent.type.name))
          ? 0
          : 1000,
    },
  ],
  edgeDetection: "none",
};

// floating-ui 按悬停的块计算 left-start 位置，这个 middleware 把 x 平移到编辑区的左边缘，手柄因此始终在同一列。
// floating-ui computes the left-start position from the hovered block; this middleware shifts x to the editing
// area's left edge, so the handle always stays in one column.
function alignToContentLeft(editor: Editor): Middleware {
  return {
    name: "alignToContentLeft",
    fn: ({ x, elements }) => ({
      x:
        x +
        editor.view.dom.getBoundingClientRect().left -
        elements.reference.getBoundingClientRect().left,
    }),
  };
}

// left-start 使手柄顶部与块外框顶部对齐，这个 middleware 把手柄移到第一行的中心。插件先调用 onNodeChange 再计算位置，
// position 中已是悬停块的位置。
// left-start aligns the handle's top with the block box's top; this middleware moves the handle to the first line's
// center. The plugin calls onNodeChange before computing the position, so position holds the hovered block's position.
function centerOnFirstLine(editor: Editor, position: RefObject<number>): Middleware {
  return {
    name: "centerOnFirstLine",
    fn: ({ y, rects }) => {
      const block = editor.view.nodeDOM(position.current);
      if (!(block instanceof HTMLElement)) {
        throw new Error(`The block at ${position.current} has no element.`);
      }
      const line = firstLineRect(block);
      return {
        y:
          y +
          (line.top + line.height / 2 - block.getBoundingClientRect().top) -
          rects.floating.height / 2,
      };
    },
  };
}

export function BlockHandle({ editor }: { editor: Editor }) {
  const [block, setBlock] = useState<{ node: ProseMirrorNode; pos: number } | null>(null);
  const [open, setOpen] = useState(false);
  const position = useRef(-1);
  // 配置对象保持不变，DragHandle 才不会重建插件。A stable config keeps DragHandle from recreating its plugin.
  const positionConfig = useMemo<Partial<ComputePositionConfig>>(
    () => ({ middleware: [alignToContentLeft(editor), centerOnFirstLine(editor, position)] }),
    [editor],
  );

  // 菜单打开时锁定手柄，鼠标离开也不隐藏、不换块。The handle is locked while the menu is open, so it neither hides
  // nor moves to another block.
  const setMenuOpen = (next: boolean) => {
    setOpen(next);
    editor.view.dispatch(editor.state.tr.setMeta("lockDragHandle", next));
  };
  const act = (action: (pos: number) => void) => {
    setMenuOpen(false);
    if (block === null) return;
    action(block.pos);
    editor.view.focus();
  };

  return (
    <DragHandle
      editor={editor}
      nested={NESTED}
      computePositionConfig={positionConfig}
      onNodeChange={({ node, pos }) => {
        position.current = pos;
        setBlock(node === null ? null : { node, pos });
      }}
    >
      <PopupButton
        label="⋮⋮"
        title="Drag to move, click for options"
        placement="below"
        open={open}
        onOpenChange={setMenuOpen}
      >
        {block !== null && canTurnInto(block.node) ? (
          <>
            <p className="popup-heading">Turn into</p>
            {TURN_INTO_ITEMS.map((item) => (
              <button
                key={item.type}
                type="button"
                role="menuitem"
                onClick={() =>
                  act((pos) => {
                    selectBlock(editor, pos);
                    turnInto(editor, item.type);
                  })
                }
              >
                {item.title}
              </button>
            ))}
          </>
        ) : null}
        {block !== null && canDuplicate(block.node) ? (
          <button
            type="button"
            role="menuitem"
            onClick={() => act((pos) => duplicateBlock(editor, pos))}
          >
            Duplicate
          </button>
        ) : null}
        <button
          type="button"
          role="menuitem"
          onClick={() => act((pos) => deleteBlock(editor, pos))}
        >
          Delete
        </button>
      </PopupButton>
    </DragHandle>
  );
}
