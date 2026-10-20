import type { ExtensionApi } from "@agw/extension-sdk";
import type { Editor } from "@tiptap/core";
import { useEditorState } from "@tiptap/react";
import { BubbleMenu } from "@tiptap/react/menus";
import { useEffect, useState, type ComponentProps } from "react";

import type { EditUi } from "./edit-ui";
import { applyLink, currentLink, openLink, removeLink } from "./link-commands";
import { AddressForm, hasFocus, MenuButton } from "./menu-parts";
import { MISSING_DEFINITION } from "./reference-decorations";

// 链接浮层（设计 5.7）：光标在链接中时显示地址与 Open、Edit、Remove，引用式链接同时显示标签；Edit 与 `Mod+K` 打开地址
// 输入框（Apply、Remove）。
// The link popover (design section 5.7): with the cursor in a link it shows the address with Open, Edit and Remove,
// plus the label of a reference link; Edit and `Mod+K` open the address input (Apply, Remove).

// BubbleMenu 的 options 或 shouldShow 换了对象时会派发一个事务，所以两者在模块中只创建一次。
// BubbleMenu dispatches a transaction whenever options or shouldShow is a new object, so both are created once here.
const OPTIONS: ComponentProps<typeof BubbleMenu>["options"] = { placement: "bottom-start" };
const shouldShow: ComponentProps<typeof BubbleMenu>["shouldShow"] = ({ view, state, element }) =>
  currentLink(state) !== null && hasFocus(view, element);

export function LinkMenu({
  api,
  editor,
  ui,
  fileUri,
}: {
  api: ExtensionApi;
  editor: Editor;
  ui: EditUi;
  fileUri(): string;
}) {
  const [editing, setEditing] = useState(false);
  const link = useEditorState({
    editor,
    selector: ({ editor: current }) => currentLink(current.state),
  });

  // 光标离开这个链接后回到地址显示。Leaving this link returns to the address view.
  useEffect(() => setEditing(false), [link?.from, link?.to]);

  useEffect(() => {
    const subscription = ui.onLinkEditor(() => {
      if (currentLink(editor.state)) setEditing(true);
    });
    return () => subscription.dispose();
  }, [editor, ui]);

  const close = () => {
    setEditing(false);
    editor.view.focus();
  };

  return (
    <BubbleMenu editor={editor} className="link-menu" options={OPTIONS} shouldShow={shouldShow}>
      {link === null ? null : editing ? (
        <AddressForm
          label="Link address"
          initial={link.href ?? ""}
          onApply={(href) => {
            if (href) applyLink(editor, href);
            else removeLink(editor);
            close();
          }}
          onRemove={() => {
            removeLink(editor);
            close();
          }}
          onCancel={close}
        />
      ) : (
        <>
          <span
            className={link.href === null ? "link-address missing-link-definition" : "link-address"}
          >
            {link.href ?? MISSING_DEFINITION}
          </span>
          {link.reference !== null ? <span className="link-label">[{link.reference}]</span> : null}
          <MenuButton
            label="Open"
            disabled={!link.href}
            onClick={() => openLink(api, fileUri(), link.href!)}
          />
          <MenuButton label="Edit" onClick={() => setEditing(true)} />
          <MenuButton
            label="Remove"
            onClick={() => {
              removeLink(editor);
              editor.view.focus();
            }}
          />
        </>
      )}
    </BubbleMenu>
  );
}
