import assert from "node:assert/strict";
import test from "node:test";

import { zoneFrameBox } from "./overlay-geometry";

test("zoneFrameBox_WithScrollbarAndMinimap_SpansFromTextToScrollbar", () => {
  assert.deepEqual(
    zoneFrameBox({
      width: 800,
      contentLeft: 64,
      verticalScrollbarWidth: 14,
      minimap: { minimapWidth: 0 },
    }),
    { left: 64, width: 722 },
  );
  assert.deepEqual(
    zoneFrameBox({
      width: 800,
      contentLeft: 64,
      verticalScrollbarWidth: 14,
      minimap: { minimapWidth: 100 },
    }),
    { left: 64, width: 622 },
  );
});

test("zoneFrameBox_EditorNarrowerThanGutter_HasZeroWidth", () => {
  assert.deepEqual(
    zoneFrameBox({
      width: 40,
      contentLeft: 64,
      verticalScrollbarWidth: 14,
      minimap: { minimapWidth: 0 },
    }),
    { left: 64, width: 0 },
  );
});
