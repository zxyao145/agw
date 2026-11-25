import assert from "node:assert/strict";
import test from "node:test";
import type { MenuItemConstructorOptions } from "electron";

import type { WorkspaceCommand } from "../shared/contracts";
import { buildApplicationMenuTemplate, workspaceMenuAction } from "./app-menu";

function item(template: MenuItemConstructorOptions[], label: string): MenuItemConstructorOptions {
  for (const entry of template) {
    const found = Array.isArray(entry.submenu)
      ? entry.submenu.find((child) => child.label === label)
      : undefined;
    if (found) return found;
  }
  throw new Error(`No menu item ${label}.`);
}

test("workspace menu commands target the main host when its window has focus", () => {
  const main = { id: 1, webContents: { id: 10 } };
  const focused = { id: main.id, webContents: { id: 20 } };
  for (const command of ["closeTab", "nextTab", "previousTab"] as const) {
    const action = workspaceMenuAction(command, focused, main);
    assert.equal(action?.kind, "workspace");
    assert.equal(action.contents, main.webContents);
    assert.equal(action.command, command);
  }
});

test("Close Tab closes the focused auxiliary window and tab navigation leaves it alone", () => {
  const main = { id: 1, webContents: { id: 10 } };
  const auxiliary = { id: 2 };
  assert.deepEqual(workspaceMenuAction("closeTab", auxiliary, main), {
    kind: "closeWindow",
    window: auxiliary,
  });
  assert.equal(workspaceMenuAction("nextTab", auxiliary, main), null);
  assert.equal(workspaceMenuAction("previousTab", auxiliary, main), null);
  assert.equal(workspaceMenuAction("closeTab", null, main), null);
});

for (const platform of ["darwin", "win32", "linux"] as const) {
  test(`the ${platform} menu sends tab commands with their accelerators`, () => {
    const sent: WorkspaceCommand[] = [];
    const template = buildApplicationMenuTemplate(platform, (command) => sent.push(command));
    assert.equal(item(template, "Close Tab").accelerator, "CmdOrCtrl+W");
    assert.equal(item(template, "Close Window").accelerator, "Shift+CmdOrCtrl+W");
    assert.equal(item(template, "Close Window").role, "close");
    assert.equal(item(template, "Next Tab").accelerator, "Ctrl+Tab");
    assert.equal(item(template, "Previous Tab").accelerator, "Ctrl+Shift+Tab");
    for (const label of ["Close Tab", "Next Tab", "Previous Tab"]) {
      (item(template, label).click as () => void)();
    }
    assert.deepEqual(sent, ["closeTab", "nextTab", "previousTab"]);
    assert.ok(template.some((entry) => entry.role === "editMenu"));
    assert.ok(template.some((entry) => entry.role === "viewMenu"));
    assert.equal(
      template.some((entry) => entry.role === "appMenu"),
      platform === "darwin",
    );
  });
}
