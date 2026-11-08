import type { Editor } from "@tiptap/core";
import type { EditorState } from "@tiptap/pm/state";
import { BubbleMenu } from "@tiptap/react/menus";
import { Tooltip } from "radix-ui";
import { useCallback, type ComponentProps } from "react";

import { hasFocus, MenuButton } from "./menu-parts";
import { TABLE_ACTIONS } from "./table-commands";

// 表格工具条（设计 5.7）：光标在表格中时显示在表格上方，每个操作是一个图标按钮，悬停时 tooltip 显示操作名称。
// The table toolbar (design section 5.7): shown above the table while the cursor is in it; each operation is an icon
// button whose tooltip shows the operation's name on hover.

/** 选区所在表格的位置；不在表格中时为 null。The position of the table around the selection, or null. */
function tablePosition(state: EditorState): number | null {
  const { $from } = state.selection;
  for (let depth = $from.depth; depth > 0; depth -= 1) {
    if ($from.node(depth).type.name === "table") return $from.before(depth);
  }
  return null;
}

// BubbleMenu 的 options、shouldShow 或 getReferencedVirtualElement 换了对象时会派发一个事务，所以它们只创建一次。
// BubbleMenu dispatches a transaction whenever options, shouldShow or getReferencedVirtualElement is a new object, so
// they are created once.
const OPTIONS: ComponentProps<typeof BubbleMenu>["options"] = {
  placement: "top-start",
  flip: false,
};
const shouldShow: ComponentProps<typeof BubbleMenu>["shouldShow"] = ({ view, state, element }) =>
  tablePosition(state) !== null && hasFocus(view, element);

export function TableToolbar({ editor }: { editor: Editor }) {
  const referencedElement = useCallback(() => {
    const position = tablePosition(editor.state);
    if (position === null) return null;
    const table = editor.view.nodeDOM(position) as HTMLElement | null;
    if (!table) return null;
    return { getBoundingClientRect: () => table.getBoundingClientRect() };
  }, [editor]);
  return (
    <BubbleMenu
      editor={editor}
      className="table-toolbar"
      options={OPTIONS}
      shouldShow={shouldShow}
      getReferencedVirtualElement={referencedElement}
    >
      <Tooltip.Provider delayDuration={0}>
        {TABLE_ACTIONS.map((action) => (
          <Tooltip.Root key={action.title}>
            <Tooltip.Trigger asChild>
              <MenuButton
                label={<action.icon size={16} aria-hidden />}
                aria-label={action.title}
                onClick={() => {
                  action.run(editor);
                  editor.view.focus();
                }}
              />
            </Tooltip.Trigger>
            <Tooltip.Portal>
              <Tooltip.Content className="tooltip" side="top" sideOffset={6}>
                {action.title}
                <Tooltip.Arrow className="tooltip-arrow" />
              </Tooltip.Content>
            </Tooltip.Portal>
          </Tooltip.Root>
        ))}
      </Tooltip.Provider>
    </BubbleMenu>
  );
}
