import assert from "node:assert/strict";
import { afterEach, beforeEach, test } from "node:test";

import { settle } from "../editor/test-edit-host";
import { connectTestHost, type TestHost } from "../test-host";
import { applyWorkspaceBackdrop } from "./workspace-backdrop";

const GRADIENT = "radial-gradient(circle 20vmax at 58% 4%, #fbe5d8 0 99%, transparent 100%)";
const WARM = { id: "warm-minimalism-theme:warm-minimalism", title: "Warm Minimalism" };

let host: TestHost;
beforeEach(() => {
  host = connectTestHost(() => null);
  document.documentElement.removeAttribute("data-workspace-backdrop");
});
afterEach(() => host.close());

function hasBackdrop(): boolean {
  return document.documentElement.hasAttribute("data-workspace-backdrop");
}

test("applyWorkspaceBackdrop marks the page only while the theme provides a backdrop", async () => {
  applyWorkspaceBackdrop(host.api);
  assert.equal(hasBackdrop(), false);

  host.changeTheme({ ...WARM, kind: "light", tokens: { "--agw-workspace-backdrop": GRADIENT } });
  await settle();
  assert.equal(hasBackdrop(), true);

  host.changeTheme({ ...WARM, kind: "dark", tokens: { "--agw-workspace-backdrop": "none" } });
  await settle();
  assert.equal(hasBackdrop(), false);
});

test("applyWorkspaceBackdrop marks the page at once when the current theme already provides a backdrop", async () => {
  host.changeTheme({ ...WARM, kind: "light", tokens: { "--agw-workspace-backdrop": GRADIENT } });
  await settle();
  applyWorkspaceBackdrop(host.api);
  assert.equal(hasBackdrop(), true);
});
