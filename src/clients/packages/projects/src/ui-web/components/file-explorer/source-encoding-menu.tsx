import * as React from "react";
import { POPUP_ITEM_CLASS, POPUP_NOTE_CLASS, PopupButton } from "@agw/code-view";

import type { SourceDocumentState, SourceEncoding } from "./source-document";

// 只有这三种编码可以带 BOM。Only these three encodings can have a BOM.
const BOM_ENCODINGS: ReadonlySet<string> = new Set(["utf-8", "utf-16le", "utf-16be"]);

type MenuState =
  | { view: "closed" }
  | { view: "actions" }
  | { view: "encodings"; mode: "reopen" | "save"; encodings: readonly SourceEncoding[] | null };

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

/**
 * 状态栏的编码菜单（设计文档 5.4 节）：按钮显示编码名称，"Reopen with Encoding…" 与 "Save with Encoding…" 打开编码
 * 列表；保存时可以带 BOM 的编码另有 "with BOM" 一项。没有编码（读取中或失败）时不显示。
 * The status bar's encoding menu (design section 5.4): the button shows the encoding
 * name; "Reopen with Encoding…" and "Save with Encoding…" open the encoding list, where saving lists a "with BOM"
 * entry for encodings that can have one. Nothing shows without an encoding (loading or failed).
 */
export function SourceEncodingMenu({ document }: { document: SourceDocumentState }) {
  const [menu, setMenu] = React.useState<MenuState>({ view: "closed" });
  const close = () => setMenu({ view: "closed" });
  if (document.encodingLabel === null) return null;

  const choose = (mode: "reopen" | "save") => {
    setMenu({ view: "encodings", mode, encodings: null });
    document.listEncodings().then(
      (encodings) =>
        setMenu((current) =>
          current.view === "encodings" && current.mode === mode
            ? { view: "encodings", mode, encodings }
            : current,
        ),
      (error: unknown) => {
        close();
        document.notifyError(errorMessage(error));
      },
    );
  };

  let items: React.ReactNode = null;
  if (menu.view === "actions") {
    items = (
      <>
        <button
          type="button"
          role="menuitem"
          className={POPUP_ITEM_CLASS}
          onClick={() => choose("reopen")}
        >
          Reopen with Encoding…
        </button>
        <button
          type="button"
          role="menuitem"
          className={POPUP_ITEM_CLASS}
          onClick={() => choose("save")}
        >
          Save with Encoding…
        </button>
      </>
    );
  } else if (menu.view === "encodings") {
    items = (
      <>
        <p className={`${POPUP_NOTE_CLASS} font-semibold`}>
          {menu.mode === "reopen" ? "Reopen with Encoding" : "Save with Encoding"}
        </p>
        {menu.encodings === null ? (
          <p className={POPUP_NOTE_CLASS}>Loading…</p>
        ) : menu.mode === "reopen" ? (
          menu.encodings.map((encoding) => (
            <button
              key={encoding.id}
              type="button"
              role="menuitem"
              className={POPUP_ITEM_CLASS}
              data-encoding={encoding.id}
              onClick={() => {
                close();
                document.reopenWithEncoding(encoding.id);
              }}
            >
              {encoding.label}
            </button>
          ))
        ) : (
          menu.encodings.flatMap((encoding) =>
            [false, ...(BOM_ENCODINGS.has(encoding.id) ? [true] : [])].map((bom) => (
              <button
                key={`${encoding.id}:${bom}`}
                type="button"
                role="menuitem"
                className={POPUP_ITEM_CLASS}
                data-encoding={encoding.id}
                data-bom={String(bom)}
                onClick={() => {
                  close();
                  document.saveWithEncoding(encoding.id, bom);
                }}
              >
                {bom ? `${encoding.label} with BOM` : encoding.label}
              </button>
            )),
          )
        )}
      </>
    );
  }

  return (
    <PopupButton
      label={document.encodingLabel}
      open={menu.view !== "closed"}
      onOpenChange={(open) => setMenu(open ? { view: "actions" } : { view: "closed" })}
    >
      {items}
    </PopupButton>
  );
}
