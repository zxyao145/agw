import {
  DropdownMenuCheckboxItem,
  DropdownMenuItem,
  DropdownMenuPortal,
  DropdownMenuSub,
  DropdownMenuSubContent,
  DropdownMenuSubTrigger,
} from "@agw/components";

/**
 * 查看器加入文件标题栏菜单的一项（Desktop 中由 extension 声明）；带 `menu` 时为子菜单，提供 `checked` 时为勾选项。
 * An entry a viewer adds to a file header menu (declared by an extension in Desktop); with `menu` it is a submenu, and
 * with `checked` a checkbox item.
 */
export type FileViewerMenuEntry = {
  id: string;
  label: string;
  disabled?: boolean;
  checked?: boolean;
  menu?: readonly FileViewerMenuEntry[];
};

/**
 * 下拉菜单中的菜单项：没有 `menu` 的项被选择时调用 `onSelect`，带 `menu` 的项为子菜单，超出可用高度时滚动；
 * `disabled` 的项不能选择。
 * Entries inside a dropdown menu: choosing an entry without `menu` calls `onSelect`; an entry with `menu` is a submenu
 * that scrolls beyond the available height; `disabled` entries cannot be chosen.
 */
export function FileViewerMenuEntries({
  entries,
  onSelect,
}: {
  entries: readonly FileViewerMenuEntry[];
  onSelect(entryId: string): void;
}) {
  return (
    <>
      {entries.map((entry) =>
        entry.menu ? (
          <DropdownMenuSub key={entry.id}>
            <DropdownMenuSubTrigger className="cursor-pointer" disabled={entry.disabled}>
              {entry.label}
            </DropdownMenuSubTrigger>
            <DropdownMenuPortal>
              <DropdownMenuSubContent className="max-h-(--radix-dropdown-menu-content-available-height) overflow-y-auto agw-scrollbar">
                <FileViewerMenuEntries entries={entry.menu} onSelect={onSelect} />
              </DropdownMenuSubContent>
            </DropdownMenuPortal>
          </DropdownMenuSub>
        ) : entry.checked !== undefined ? (
          <DropdownMenuCheckboxItem
            key={entry.id}
            className="cursor-pointer"
            checked={entry.checked}
            disabled={entry.disabled}
            onSelect={() => onSelect(entry.id)}
          >
            {entry.label}
          </DropdownMenuCheckboxItem>
        ) : (
          <DropdownMenuItem
            key={entry.id}
            className="cursor-pointer"
            disabled={entry.disabled}
            onSelect={() => onSelect(entry.id)}
          >
            {entry.label}
          </DropdownMenuItem>
        ),
      )}
    </>
  );
}
