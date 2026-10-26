import type { Editor, EditorEvents } from "@tiptap/core";
import { NodeSelection } from "@tiptap/pm/state";
import { useEditorState } from "@tiptap/react";
import { BubbleMenu } from "@tiptap/react/menus";
import { useEffect, useLayoutEffect, useRef, useState, type ComponentProps } from "react";

import type { EditUi } from "./edit-ui";
import { imageAddress, insertImage, mapImageInsertPosition, updateImage } from "./image-commands";
import { AddressForm, hasFocus } from "./menu-parts";

// 图片的浮层（设计 5.7）：选中图片时可以修改地址与 alt；`/` 菜单的 Image 在光标处打开地址输入框。
// Image popovers (design section 5.7): a selected image's address and alt text can be changed; the `/` menu's Image
// opens an address input at the cursor.

type SelectedImage = { pos: number; src: string; alt: string };

function selectedImage(editor: Editor): SelectedImage | null {
  const { selection } = editor.state;
  if (!(selection instanceof NodeSelection) || selection.node.type.name !== "image") return null;
  return {
    pos: selection.from,
    src: imageAddress(editor, selection.node),
    alt: String(selection.node.attrs.alt ?? ""),
  };
}

// BubbleMenu 的 options 或 shouldShow 换了对象时会派发一个事务，所以两者在模块中只创建一次。
// BubbleMenu dispatches a transaction whenever options or shouldShow is a new object, so both are created once here.
const OPTIONS: ComponentProps<typeof BubbleMenu>["options"] = { placement: "bottom-start" };
const shouldShow: ComponentProps<typeof BubbleMenu>["shouldShow"] = ({ editor, view, element }) =>
  selectedImage(editor) !== null && hasFocus(view, element);

function AltField({ value, onChange }: { value: string; onChange(value: string): void }) {
  return (
    <input
      aria-label="Alt text"
      placeholder="Alt text"
      value={value}
      onChange={(event) => onChange(event.target.value)}
    />
  );
}

export function ImageMenu({ editor }: { editor: Editor }) {
  const image = useEditorState({
    editor,
    selector: ({ editor: current }) => selectedImage(current),
  });
  const [alt, setAlt] = useState("");
  useEffect(() => setAlt(image?.alt ?? ""), [image?.pos, image?.alt]);

  return (
    <BubbleMenu editor={editor} className="image-menu" options={OPTIONS} shouldShow={shouldShow}>
      {image === null ? null : (
        <AddressForm
          key={image.pos}
          label="Image address"
          initial={image.src}
          onApply={(src) => {
            updateImage(editor, image.pos, { src, alt });
            editor.view.focus();
          }}
          onCancel={() => editor.view.focus()}
        >
          <AltField value={alt} onChange={setAlt} />
        </AddressForm>
      )}
    </BubbleMenu>
  );
}

/** `/` 菜单的 Image：在请求的位置显示地址输入框，确认后插入图片。The `/` menu's Image: an address input at the
 *  requested position that inserts the image on Apply. */
export function ImageInsert({ editor, ui }: { editor: Editor; ui: EditUi }) {
  const [position, setPosition] = useState<number | null>(null);
  const elementRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const subscription = ui.onImageInsert(setPosition);
    return () => subscription.dispose();
  }, [ui]);

  // 输入框打开时，记录的位置随之后的编辑移动，位置不在了时输入框关闭。While the input is open the recorded position
  // moves with later edits, and the input closes once the position is gone.
  const open = position !== null;
  useEffect(() => {
    if (!open) return;
    const onTransaction = ({ transaction, appendedTransactions }: EditorEvents["transaction"]) => {
      const transactions = [transaction, ...appendedTransactions];
      if (!transactions.some((tr) => tr.docChanged)) return;
      setPosition((current) =>
        current === null ? null : mapImageInsertPosition(current, transactions),
      );
    };
    editor.on("transaction", onTransaction);
    return () => {
      editor.off("transaction", onTransaction);
    };
  }, [editor, open]);

  // 放在光标下方。Placed below the cursor.
  useLayoutEffect(() => {
    if (position === null || !elementRef.current) return;
    const coords = editor.view.coordsAtPos(position);
    elementRef.current.style.left = `${coords.left}px`;
    elementRef.current.style.top = `${coords.bottom + 4}px`;
  }, [editor, position]);

  if (position === null) return null;
  const close = () => {
    setPosition(null);
    editor.view.focus();
  };
  return (
    <div ref={elementRef} className="image-insert">
      <AddressForm
        label="Image address"
        initial=""
        onApply={(src) => {
          if (src) insertImage(editor, position, src);
          close();
        }}
        onCancel={close}
      />
    </div>
  );
}
