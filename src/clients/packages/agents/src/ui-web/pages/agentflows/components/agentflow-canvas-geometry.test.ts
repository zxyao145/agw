import assert from "node:assert/strict";
import test from "node:test";

import {
  AGENTFLOW_GRID_SIZE,
  AGENTFLOW_NODE_SIZE,
  estimateAgentflowNodeHeight,
  isCanvasRectVisible,
  snapAgentflowPosition,
} from "./agentflow-canvas-geometry";

test("canvas geometry shares the node size and grid and snaps positions", () => {
  assert.deepEqual(AGENTFLOW_NODE_SIZE, { width: 240, height: 64 });
  assert.equal(AGENTFLOW_GRID_SIZE, 20);
  assert.deepEqual(snapAgentflowPosition({ x: 29, y: 31 }), { x: 20, y: 40 });
});

test("node height estimates use the shared default and member row height", () => {
  assert.equal(estimateAgentflowNodeHeight(undefined), AGENTFLOW_NODE_SIZE.height);
});

test("canvas visibility accounts for viewport translation and zoom", () => {
  const canvasSize = { width: 800, height: 600 };
  const viewport = { x: -100, y: -50, zoom: 1 };
  const visibleRect = { x: 200, y: 100, width: 120, height: 70 };
  const offscreenRect = { x: 900, y: 100, width: 120, height: 70 };

  assert.equal(isCanvasRectVisible(visibleRect, viewport, canvasSize), true);
  assert.equal(isCanvasRectVisible(offscreenRect, viewport, canvasSize), false);
  assert.equal(isCanvasRectVisible(visibleRect, { x: -100, y: -50, zoom: 4 }, canvasSize), false);
});
