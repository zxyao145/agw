import assert from "node:assert/strict";
import test from "node:test";

import { THEME_TOKEN_NAMES } from "./extension-host-provider";

test("the theme offered to extensions includes the host's monospace font variable", () => {
  assert.ok(THEME_TOKEN_NAMES.includes("--font-mono"));
});
