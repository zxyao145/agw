import assert from "node:assert/strict";
import test from "node:test";

import { dockPosition, positionAfterResize, positionForDrag } from "./floating-chat-geometry";

test("drag position follows screen-coordinate deltas and stays inside the client area", () => {
  const start = { x: 64, y: 80, screenX: 300, screenY: 250 };

  assert.deepEqual(positionForDrag(start, 260, 275, { width: 360, height: 300 }), {
    x: 24,
    y: 105,
  });
  assert.deepEqual(positionForDrag(start, 1000, -50, { width: 360, height: 300 }), {
    x: 308,
    y: 0,
  });
});

test("dock position chooses the nearest horizontal edge and clamps vertical position", () => {
  assert.deepEqual(dockPosition({ x: 170, y: 140 }, { width: 300, height: 160 }), {
    x: 248,
    y: 108,
    side: "right",
  });
  assert.deepEqual(dockPosition({ x: 30, y: -20 }, { width: 300, height: 160 }), {
    x: 0,
    y: 0,
    side: "left",
  });
});

test("resize position follows the docked edge and remains visible", () => {
  assert.deepEqual(positionAfterResize({ x: 736, y: 520 }, { width: 500, height: 300 }, "right"), {
    x: 448,
    y: 248,
  });
});
