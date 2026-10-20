import type { EditorView } from "@tiptap/pm/view";
import { useState, type FormEvent, type KeyboardEvent, type ReactNode } from "react";

// 浮层与工具条共用的部件。Parts shared by the popovers and toolbars.

/** 编辑器或浮层自身有焦点（浮层中的输入框获得焦点时浮层保持显示）。The editor or the popover itself has focus (a
 *  popover stays while its input has focus). */
export function hasFocus(view: EditorView, element: HTMLElement): boolean {
  return view.hasFocus() || element.contains(document.activeElement);
}

/** 不移走编辑器焦点与选区的按钮。A button that keeps the editor's focus and selection. */
export function MenuButton({
  label,
  title,
  active,
  disabled,
  onClick,
}: {
  label: ReactNode;
  title?: string;
  active?: boolean;
  disabled?: boolean;
  onClick(): void;
}) {
  return (
    <button
      type="button"
      className="menu-button"
      title={title}
      aria-pressed={active}
      disabled={disabled}
      onMouseDown={(event) => event.preventDefault()}
      onClick={onClick}
    >
      {label}
    </button>
  );
}

/**
 * 地址输入框：`Enter` 或 Apply 确认，`Esc` 取消；`onRemove` 存在时显示 Remove。
 * An address input: `Enter` or Apply confirms, `Esc` cancels; Remove shows when `onRemove` is given.
 */
export function AddressForm({
  label,
  initial,
  onApply,
  onRemove,
  onCancel,
  children,
}: {
  label: string;
  initial: string;
  onApply(address: string): void;
  onRemove?: () => void;
  onCancel(): void;
  children?: ReactNode;
}) {
  const [address, setAddress] = useState(initial);
  const submit = (event: FormEvent) => {
    event.preventDefault();
    onApply(address.trim());
  };
  const onKeyDown = (event: KeyboardEvent) => {
    if (event.key !== "Escape") return;
    event.preventDefault();
    onCancel();
  };
  return (
    <form className="address-form" onSubmit={submit} onKeyDown={onKeyDown}>
      <input
        aria-label={label}
        placeholder={label}
        value={address}
        autoFocus
        onChange={(event) => setAddress(event.target.value)}
      />
      {children}
      <button type="submit" className="menu-button">
        Apply
      </button>
      {onRemove ? (
        <button type="button" className="menu-button" onClick={onRemove}>
          Remove
        </button>
      ) : null}
    </form>
  );
}
