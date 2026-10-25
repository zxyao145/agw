"use client";

import * as React from "react";

/** 状态栏按钮的样式。The status bar button style. */
export const STATUS_BUTTON_CLASS =
  "h-6 cursor-pointer whitespace-nowrap rounded-md border border-transparent bg-transparent px-2 text-inherit hover:bg-accent hover:text-accent-foreground aria-expanded:bg-accent aria-expanded:text-accent-foreground disabled:cursor-default disabled:opacity-50";

/** 弹出菜单中按钮的样式。The style of buttons in popup menus. */
export const POPUP_ITEM_CLASS =
  "cursor-pointer whitespace-nowrap rounded-sm border-0 bg-transparent px-2 py-1 text-left text-inherit hover:bg-accent hover:text-accent-foreground focus-visible:bg-accent focus-visible:text-accent-foreground focus-visible:outline-none";

/** 弹出菜单中标题与说明的样式。The style of headings and notes in popup menus. */
export const POPUP_NOTE_CLASS = "m-0 whitespace-nowrap px-2 py-1 text-muted-foreground";

/**
 * 状态栏的按钮与它打开的弹出菜单（在按钮上方）；点击外部或按 `Esc` 时关闭。
 * A status bar button and the popup menu it opens above it; clicking outside or pressing `Esc` closes it.
 */
export function PopupButton({
  label,
  open,
  onOpenChange,
  children,
}: {
  label: React.ReactNode;
  open: boolean;
  onOpenChange(open: boolean): void;
  children: React.ReactNode;
}) {
  const rootRef = React.useRef<HTMLDivElement>(null);
  React.useEffect(() => {
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
    <div className="relative" ref={rootRef}>
      <button
        type="button"
        className={STATUS_BUTTON_CLASS}
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={() => onOpenChange(!open)}
      >
        {label}
      </button>
      {open ? (
        <div
          role="menu"
          className="absolute bottom-[calc(100%+4px)] right-0 z-10 flex max-h-80 min-w-[200px] flex-col overflow-y-auto rounded-md border border-border bg-popover p-1 text-popover-foreground shadow-[0_4px_16px_rgb(0_0_0/0.16)]"
        >
          {children}
        </div>
      ) : null}
    </div>
  );
}
