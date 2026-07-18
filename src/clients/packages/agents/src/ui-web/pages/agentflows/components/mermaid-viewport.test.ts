import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const AGENTFLOWS_PAGE_URL = new URL("../page.tsx", import.meta.url);

async function loadMermaidViewport() {
  return await import("./mermaid-viewport" + ".ts");
}

test("zoomViewport keeps the cursor anchored while scaling", async () => {
  const { zoomViewport } = await loadMermaidViewport();

  const next = zoomViewport({
    viewport: { scale: 1, x: 0, y: 0 },
    cursor: { x: 300, y: 180 },
    deltaY: -120,
  });

  const contentXBefore = (300 - 0) / 1;
  const contentYBefore = (180 - 0) / 1;
  const screenXAfter = contentXBefore * next.scale + next.x;
  const screenYAfter = contentYBefore * next.scale + next.y;

  assert.equal(screenXAfter, 300);
  assert.equal(screenYAfter, 180);
  assert.ok(next.scale > 1);
});

test("panViewport adds pointer movement to the current offset", async () => {
  const { panViewport } = await loadMermaidViewport();

  assert.deepEqual(
    panViewport({
      viewport: { scale: 1.25, x: 40, y: -20 },
      movement: { x: -12, y: 30 },
    }),
    { scale: 1.25, x: 28, y: 10 },
  );
});

test("zoomViewport clamps scale to the supported range", async () => {
  const { zoomViewport } = await loadMermaidViewport();

  const zoomedOut = zoomViewport({
    viewport: { scale: 0.25, x: 0, y: 0 },
    cursor: { x: 0, y: 0 },
    deltaY: 5000,
  });
  const zoomedIn = zoomViewport({
    viewport: { scale: 3.5, x: 0, y: 0 },
    cursor: { x: 0, y: 0 },
    deltaY: -5000,
  });

  assert.equal(zoomedOut.scale, 0.25);
  assert.equal(zoomedIn.scale, 4);
});

test("Mermaid wheel zoom uses a non-passive native listener", async () => {
  const source = await readFile(AGENTFLOWS_PAGE_URL, "utf8");

  assert.match(source, /addEventListener\("wheel", handleMermaidWheel, \{ passive: false \}\)/);
  assert.doesNotMatch(source, /onWheel=\{handleMermaidWheel\}/);
});
