import assert from "node:assert/strict";
import test from "node:test";

import type { InstalledExtension } from "@desktop/shared/contracts";
import { extensionsInCategory, themeTitles } from "./extension-categories";
import type { ExtensionState } from "./extension-host";
import { sampleExtension } from "./extension-host.test-helper";

function extension(installed: InstalledExtension): ExtensionState {
  return {
    id: installed.id,
    installed,
    status: "inactive",
    error: null,
    blockedRequests: [],
    frames: [],
  };
}

const viewer = extension(sampleExtension([]));
const themeOnly = extension({
  ...sampleExtension([]),
  id: "soft-theme",
  enabled: false,
  manifest: {
    manifestVersion: 1,
    id: "soft-theme",
    name: "Soft Theme",
    version: "1.0.0",
    contributes: { themes: [{ id: "soft", title: "Soft", path: "themes/soft.json" }] },
  },
});
const themeWithViews = extension({
  ...sampleExtension([]),
  id: "studio",
  validation: "invalid",
  manifest: {
    manifestVersion: 1,
    id: "studio",
    name: "Studio",
    version: "1.0.0",
    contributes: {
      mainViews: [{ id: "app", title: "App", icon: "Box", entry: "index.html" }],
      themes: [
        { id: "day", title: "Day", path: "themes/day.json" },
        { id: "night", title: "Night", path: "themes/night.json" },
      ],
    },
  },
});
const all = [themeOnly, viewer, themeWithViews];

test("category=themes lists only extensions that contribute themes, enabled or not", () => {
  assert.deepEqual(
    extensionsInCategory(all, "themes")?.map((item) => item.id),
    ["soft-theme", "studio"],
  );
});

test("no category lists every extension and an unknown category lists none", () => {
  assert.deepEqual(extensionsInCategory(all, null), all);
  assert.equal(extensionsInCategory(all, "views"), null);
  assert.equal(extensionsInCategory(all, "toString"), null);
});

test("theme titles come from the manifest", () => {
  assert.deepEqual(themeTitles(themeWithViews.installed.manifest), ["Day", "Night"]);
  assert.deepEqual(themeTitles(viewer.installed.manifest), []);
  assert.deepEqual(themeTitles(null), []);
});
