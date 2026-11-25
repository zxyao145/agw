import type { BaseWindow, MenuItemConstructorOptions } from "electron";

import type { WorkspaceCommand } from "../shared/contracts";

export function buildApplicationMenuTemplate(
  platform: NodeJS.Platform,
  run: (command: WorkspaceCommand, focusedWindow: BaseWindow | null | undefined) => void,
): MenuItemConstructorOptions[] {
  const mac = platform === "darwin";
  const click =
    (command: WorkspaceCommand): MenuItemConstructorOptions["click"] =>
    (_item, focusedWindow) =>
      run(command, focusedWindow);
  return [
    ...(mac ? [{ role: "appMenu" } as const] : []),
    {
      label: "File",
      submenu: [
        { label: "Close Tab", accelerator: "CmdOrCtrl+W", click: click("closeTab") },
        { label: "Close Window", accelerator: "Shift+CmdOrCtrl+W", role: "close" },
        ...(mac ? [] : [{ type: "separator" } as const, { role: "quit" } as const]),
      ],
    },
    { role: "editMenu" },
    { role: "viewMenu" },
    {
      label: "Window",
      submenu: [
        { role: "minimize" },
        ...(mac ? [{ role: "zoom" } as const] : []),
        { type: "separator" },
        { label: "Next Tab", accelerator: "Ctrl+Tab", click: click("nextTab") },
        { label: "Previous Tab", accelerator: "Ctrl+Shift+Tab", click: click("previousTab") },
        ...(mac ? [{ type: "separator" } as const, { role: "front" } as const] : []),
      ],
    },
  ];
}

export function workspaceMenuAction<FocusedWindow extends { id: number }, Contents>(
  command: WorkspaceCommand,
  focusedWindow: FocusedWindow | null | undefined,
  mainWindow: { id: number; webContents: Contents } | null,
) {
  if (!focusedWindow) return null;
  if (mainWindow && focusedWindow.id === mainWindow.id) {
    return { kind: "workspace", contents: mainWindow.webContents, command } as const;
  }
  return command === "closeTab" ? ({ kind: "closeWindow", window: focusedWindow } as const) : null;
}
