import type { Editor } from "@tiptap/core";
import { TextSelection } from "@tiptap/pm/state";
import { useEditorState } from "@tiptap/react";
import { BubbleMenu } from "@tiptap/react/menus";
import { useEffect, useState, type ComponentProps } from "react";

import { PopupButton } from "../shared/editor-chrome";
import { canTurnIntoSelection, TURN_INTO_ITEMS, turnInto } from "./block-commands";
import type { EditUi } from "./edit-ui";
import { applyLink, removeLink, selectionLink } from "./link-commands";
import { AddressForm, hasFocus, MenuButton } from "./menu-parts";

// 格式工具条（设计 5.7）：选中文字时显示 Bold、Italic、Strikethrough、Inline code、Link、Turn into；选区在代码块、
// `rawBlock`、`frontMatter` 中时不显示。Link 与 `Mod+K` 在工具条中打开地址输入框。
// The formatting toolbar (design section 5.7): shows Bold, Italic, Strikethrough, Inline code, Link and Turn into
// for a text selection, and nothing when the selection is in a code block, `rawBlock` or `frontMatter`. Link and
// `Mod+K` open the address input in the toolbar.

// BubbleMenu 的 options 或 shouldShow 换了对象时会派发一个事务，所以两者在模块中只创建一次。
// BubbleMenu dispatches a transaction whenever options or shouldShow is a new object, so both are created once here.
const OPTIONS: ComponentProps<typeof BubbleMenu>["options"] = { placement: "top" };
const shouldShow: ComponentProps<typeof BubbleMenu>["shouldShow"] = ({ view, state, element }) => {
  const { selection } = state;
  return (
    selection instanceof TextSelection &&
    !selection.empty &&
    !selection.$from.parent.type.spec.code &&
    !selection.$to.parent.type.spec.code &&
    hasFocus(view, element)
  );
};

export function FormattingMenu({ editor, ui }: { editor: Editor; ui: EditUi }) {
  const [editingLink, setEditingLink] = useState(false);
  const [turnIntoOpen, setTurnIntoOpen] = useState(false);
  // 输入不改变这些值，所以输入时工具条不重新渲染。Typing leaves these values unchanged, so the toolbar does not
  // render again while typing.
  const active = useEditorState({
    editor,
    selector: ({ editor: current }) => ({
      bold: current.isActive("bold"),
      italic: current.isActive("italic"),
      strike: current.isActive("strike"),
      code: current.isActive("code"),
      link: current.isActive("link"),
      turnInto: canTurnIntoSelection(current.state),
    }),
  });

  // 选区变化后回到按钮；状态没有变化时 React 不重新渲染。The buttons return when the selection changes; React does
  // not render again when the state is unchanged.
  useEffect(() => {
    const reset = () => {
      setEditingLink(false);
      setTurnIntoOpen(false);
    };
    editor.on("selectionUpdate", reset);
    return () => {
      editor.off("selectionUpdate", reset);
    };
  }, [editor]);

  useEffect(() => {
    const subscription = ui.onLinkEditor(() => {
      if (!editor.state.selection.empty) setEditingLink(true);
    });
    return () => subscription.dispose();
  }, [editor, ui]);

  const closeLink = () => {
    setEditingLink(false);
    editor.view.focus();
  };

  return (
    <BubbleMenu
      editor={editor}
      className="formatting-menu"
      options={OPTIONS}
      shouldShow={shouldShow}
    >
      {editingLink ? (
        <AddressForm
          label="Link address"
          // 选区在一个链接中时显示它的地址，引用式链接由当前定义解析。Within one link the field shows its address,
          // resolved from the current definitions for a reference link.
          initial={
            selectionLink(editor.state)?.href ?? String(editor.getAttributes("link").href ?? "")
          }
          onApply={(href) => {
            if (href) applyLink(editor, href);
            else removeLink(editor);
            closeLink();
          }}
          onRemove={
            active.link
              ? () => {
                  removeLink(editor);
                  closeLink();
                }
              : undefined
          }
          onCancel={closeLink}
        />
      ) : (
        <>
          <MenuButton
            label="Bold"
            active={active.bold}
            onClick={() => editor.chain().focus().toggleBold().run()}
          />
          <MenuButton
            label="Italic"
            active={active.italic}
            onClick={() => editor.chain().focus().toggleItalic().run()}
          />
          <MenuButton
            label="Strikethrough"
            active={active.strike}
            onClick={() => editor.chain().focus().toggleStrike().run()}
          />
          <MenuButton
            label="Inline code"
            active={active.code}
            onClick={() => editor.chain().focus().toggleCode().run()}
          />
          <MenuButton label="Link" active={active.link} onClick={() => setEditingLink(true)} />
          {/* 表格单元格中没有 Turn into。No Turn into in table cells. */}
          {active.turnInto ? (
            <PopupButton
              label="Turn into"
              placement="below"
              open={turnIntoOpen}
              onOpenChange={setTurnIntoOpen}
            >
              {TURN_INTO_ITEMS.map((item) => (
                <button
                  key={item.type}
                  type="button"
                  role="menuitem"
                  onMouseDown={(event) => event.preventDefault()}
                  onClick={() => {
                    setTurnIntoOpen(false);
                    turnInto(editor, item.type);
                    editor.view.focus();
                  }}
                >
                  {item.title}
                </button>
              ))}
            </PopupButton>
          ) : null}
        </>
      )}
    </BubbleMenu>
  );
}
