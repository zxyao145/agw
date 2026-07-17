export type MermaidViewportTransform = {
  scale: number;
  x: number;
  y: number;
};

export type MermaidViewportPoint = {
  x: number;
  y: number;
};

const MIN_SCALE = 0.25;
const MAX_SCALE = 4;
const WHEEL_ZOOM_SENSITIVITY = 0.001;

export function createDefaultMermaidViewport(): MermaidViewportTransform {
  return { scale: 1, x: 0, y: 0 };
}

export function zoomViewport({
  viewport,
  cursor,
  deltaY,
  deltaMode = 0,
}: {
  viewport: MermaidViewportTransform;
  cursor: MermaidViewportPoint;
  deltaY: number;
  deltaMode?: number;
}): MermaidViewportTransform {
  const currentScale = clamp(viewport.scale, MIN_SCALE, MAX_SCALE);
  const normalizedDeltaY = normalizeWheelDelta(deltaY, deltaMode);
  const nextScale = clamp(
    currentScale * Math.exp(-normalizedDeltaY * WHEEL_ZOOM_SENSITIVITY),
    MIN_SCALE,
    MAX_SCALE,
  );
  const scaleRatio = nextScale / currentScale;

  return {
    scale: nextScale,
    x: cursor.x - (cursor.x - viewport.x) * scaleRatio,
    y: cursor.y - (cursor.y - viewport.y) * scaleRatio,
  };
}

export function panViewport({
  viewport,
  movement,
}: {
  viewport: MermaidViewportTransform;
  movement: MermaidViewportPoint;
}): MermaidViewportTransform {
  return {
    ...viewport,
    x: viewport.x + movement.x,
    y: viewport.y + movement.y,
  };
}

function normalizeWheelDelta(deltaY: number, deltaMode: number) {
  if (deltaMode === 1) {
    return deltaY * 16;
  }

  if (deltaMode === 2) {
    return deltaY * 800;
  }

  return deltaY;
}

function clamp(value: number, min: number, max: number) {
  return Math.min(Math.max(value, min), max);
}
