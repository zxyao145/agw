import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const POINTER_LOCK_PRIMITIVES = [
  ["alert-dialog.tsx", "AlertDialog"],
  ["context-menu.tsx", "ContextMenu"],
  ["dialog.tsx", "Dialog"],
  ["dropdown-menu.tsx", "DropdownMenu"],
  ["popover.tsx", "Popover"],
  ["select.tsx", "Select"],
  ["sheet.tsx", "Dialog"],
] as const;

test("modal primitives share one Radix pointer-lock registry", async () => {
  for (const [fileName, primitive] of POINTER_LOCK_PRIMITIVES) {
    const source = await readFile(new URL(fileName, import.meta.url), "utf8");

    assert.match(
      source,
      new RegExp(`import \\{ ${primitive} as \\w+ \\} from "radix-ui";`),
      `${fileName} must import ${primitive} through radix-ui`,
    );
    assert.doesNotMatch(
      source,
      /from "@radix-ui\/react-(?:alert-dialog|context-menu|dialog|dropdown-menu|popover|select)"/,
      `${fileName} must not create a separate Radix pointer-lock registry`,
    );
  }
});
