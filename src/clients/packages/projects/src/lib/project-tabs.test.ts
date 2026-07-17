import assert from "node:assert/strict";
import test from "node:test";

// @ts-expect-error Node's type stripping requires the explicit TypeScript extension.
import { DEFAULT_PROJECT_ID, normalizeProjectTabs } from "./project-tabs.ts";

const DEFAULT_PROJECT_UUID = "11111111-1111-1111-1111-000000000001";

test("project tabs always keep the built-in project first and remove stale ids", () => {
  assert.deepEqual(
    normalizeProjectTabs(
      ["project-2", DEFAULT_PROJECT_ID, "missing", "project-2"],
      [DEFAULT_PROJECT_ID, "project-1", "project-2"],
    ),
    [DEFAULT_PROJECT_ID, "project-2"],
  );
});

test("opening a project appends it without duplicating tabs", () => {
  assert.deepEqual(
    normalizeProjectTabs(
      [DEFAULT_PROJECT_ID, "project-1"],
      [DEFAULT_PROJECT_ID, "project-1", "project-2"],
      "project-2",
    ),
    [DEFAULT_PROJECT_ID, "project-1", "project-2"],
  );
});

test("opening the built-in project replaces its legacy name tab instead of duplicating it", () => {
  assert.deepEqual(
    normalizeProjectTabs(
      ["default-built-in", DEFAULT_PROJECT_UUID],
      [DEFAULT_PROJECT_UUID, "project-1"],
      DEFAULT_PROJECT_UUID,
    ),
    [DEFAULT_PROJECT_UUID],
  );
});
