import type { DocumentSession, ExtensionApi } from "@agw/extension-sdk";
import {
  useCallback,
  useEffect,
  useRef,
  useState,
  useSyncExternalStore,
  type ReactNode,
} from "react";

import type { TextEncodingOption } from "./document-error";
import { errorMessage, notifyFailure } from "./errors";

/** 文档会话的公开状态。The public state of a document session. */
export type DocumentSessionView = Pick<
  DocumentSession,
  "file" | "state" | "format" | "changedOnDisk" | "error"
>;

function readSession(session: DocumentSession): DocumentSessionView {
  return {
    file: session.file,
    state: session.state,
    format: session.format,
    changedOnDisk: session.changedOnDisk,
    error: session.error,
  };
}

/** 文档会话的公开状态，会话变化时重新渲染。The session's public state; re-renders when the session changes. */
export function useDocumentSession(session: DocumentSession): DocumentSessionView {
  const [view, setView] = useState(() => readSession(session));
  useEffect(() => {
    // 首次渲染与订阅之间的变化也要显示。Changes between the first render and the subscription show too.
    setView(readSession(session));
    const subscription = session.onDidChange(() => setView(readSession(session)));
    return () => subscription.dispose();
  }, [session]);
  return view;
}

/**
 * 文档会话中由 `select` 选出的值，值变化时才重新渲染。`select` 返回字符串、数字、布尔值或 null。
 * 编辑器有焦点时 React 的每次提交都会在整个编辑区中计算选区位置，所以组件只订阅自己显示的值。
 * The value `select` picks from a document session; renders again only when it changes. `select` returns a string,
 * number, boolean or null. While the editor has focus every React commit computes the selection offsets over the
 * whole editing area, so components subscribe only to the values they show.
 */
export function useSessionValue<T extends string | number | boolean | null>(
  session: DocumentSession,
  select: (session: DocumentSession) => T,
): T {
  // 同一个会话沿用同一个 subscribe，重新渲染时不重新订阅。The same session keeps the same subscribe, so a render does not
  // subscribe again.
  const subscribe = useCallback(
    (onChange: () => void) => {
      const subscription = session.onDidChange(onChange);
      return () => subscription.dispose();
    },
    [session],
  );
  return useSyncExternalStore(subscribe, () => select(session));
}

/**
 * 按钮与它打开的弹出菜单；点击外部或按 `Esc` 时关闭。`placement` 为弹出层相对按钮的方向。
 * A button and the popup menu it opens; clicking outside or pressing `Esc` closes it. `placement` is the popup's side
 * of the button.
 */
export function PopupButton({
  label,
  title,
  placement,
  open,
  onOpenChange,
  children,
}: {
  label: ReactNode;
  title?: string;
  placement: "above" | "below";
  open: boolean;
  onOpenChange(open: boolean): void;
  children: ReactNode;
}) {
  const rootRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const onPointerDown = (event: PointerEvent) => {
      if (!rootRef.current?.contains(event.target as Node)) onOpenChange(false);
    };
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") onOpenChange(false);
    };
    document.addEventListener("pointerdown", onPointerDown, true);
    document.addEventListener("keydown", onKeyDown, true);
    return () => {
      document.removeEventListener("pointerdown", onPointerDown, true);
      document.removeEventListener("keydown", onKeyDown, true);
    };
  }, [open, onOpenChange]);

  return (
    <div className="popup" ref={rootRef}>
      <button
        type="button"
        className="chrome-button"
        title={title}
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={() => onOpenChange(!open)}
      >
        {label}
      </button>
      {open ? (
        <div className="popup-menu" role="menu" data-placement={placement}>
          {children}
        </div>
      ) : null}
    </div>
  );
}

// fs.writeTextDocument 只允许这三种编码带 BOM。fs.writeTextDocument allows a BOM only for these encodings.
export const BOM_ENCODINGS: ReadonlySet<string> = new Set(["utf-8", "utf-16le", "utf-16be"]);

/** 编码名称，带 BOM 时加 " with BOM"，例如 "UTF-8 with BOM"。The encoding name, e.g. "UTF-8 with BOM". */
export function encodingName(format: { encoding: string; bom: boolean }): string {
  return `${format.encoding.toUpperCase()}${format.bom ? " with BOM" : ""}`;
}

type EncodingMenuState =
  | { view: "closed" }
  | { view: "actions" }
  | { view: "encodings"; mode: "reopen" | "save"; encodings: readonly TextEncodingOption[] | null };

/**
 * 编码菜单：按钮显示编码名称，菜单项 "Reopen with Encoding…" 与 "Save with Encoding…" 打开 `fs.listTextEncodings` 的编码
 * 列表；保存时可以带 BOM 的编码另有 "with BOM" 一项。没有编码（读取中或失败）时不显示。
 * The encoding menu: the button shows the encoding name; "Reopen with Encoding…" and "Save with Encoding…" open the
 * `fs.listTextEncodings` list, where saving lists a "with BOM" entry for encodings that can have one. Nothing shows
 * without a format (loading or failed).
 */
export function EncodingMenu({
  api,
  session,
  placement,
}: {
  api: ExtensionApi;
  session: DocumentSession;
  placement: "above" | "below";
}) {
  const label = useSessionValue(session, ({ format, state }) =>
    format === null || state === "failed" ? null : encodingName(format),
  );
  const [menu, setMenu] = useState<EncodingMenuState>({ view: "closed" });
  const close = () => setMenu({ view: "closed" });
  if (label === null) return null;

  const choose = (mode: "reopen" | "save") => {
    setMenu({ view: "encodings", mode, encodings: null });
    api.fs.listTextEncodings().then(
      (encodings) =>
        setMenu((current) =>
          current.view === "encodings" && current.mode === mode
            ? { view: "encodings", mode, encodings }
            : current,
        ),
      (error: unknown) => {
        close();
        api.ui.notify({ type: "error", message: errorMessage(error) });
      },
    );
  };
  const pick = (operation: Promise<void>) => {
    close();
    notifyFailure(api, operation);
  };

  let items: ReactNode;
  if (menu.view === "actions") {
    items = (
      <>
        <button type="button" role="menuitem" onClick={() => choose("reopen")}>
          Reopen with Encoding…
        </button>
        <button type="button" role="menuitem" onClick={() => choose("save")}>
          Save with Encoding…
        </button>
      </>
    );
  } else if (menu.view === "encodings") {
    const heading = menu.mode === "reopen" ? "Reopen with Encoding" : "Save with Encoding";
    items = (
      <>
        <p className="popup-heading">{heading}</p>
        {menu.encodings === null ? (
          <p className="popup-note">Loading…</p>
        ) : menu.mode === "reopen" ? (
          menu.encodings.map((encoding) => (
            <button
              key={encoding.id}
              type="button"
              role="menuitem"
              data-encoding={encoding.id}
              onClick={() => pick(session.reopenWithEncoding(encoding.id))}
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
                data-encoding={encoding.id}
                data-bom={String(bom)}
                onClick={() => pick(session.saveWithEncoding(encoding.id, bom))}
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
      label={label}
      placement={placement}
      open={menu.view !== "closed"}
      onOpenChange={(open) => setMenu(open ? { view: "actions" } : { view: "closed" })}
    >
      {items}
    </PopupButton>
  );
}
