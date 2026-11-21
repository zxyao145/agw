export const FLOATING_CHAT_BUTTON_VIEW_SIZE = 52;

export type FloatingChatPosition = { x: number; y: number };
export type FloatingChatClientArea = { width: number; height: number };
export type FloatingChatDockSide = "left" | "right";
export type FloatingChatDragStart = FloatingChatPosition & { screenX: number; screenY: number };

function maximumPosition(area: FloatingChatClientArea): FloatingChatPosition {
  return {
    x: Math.max(0, Math.floor(area.width) - FLOATING_CHAT_BUTTON_VIEW_SIZE),
    y: Math.max(0, Math.floor(area.height) - FLOATING_CHAT_BUTTON_VIEW_SIZE),
  };
}

export function clampPosition(
  position: FloatingChatPosition,
  area: FloatingChatClientArea,
): FloatingChatPosition {
  const maximum = maximumPosition(area);
  return {
    x: Math.min(maximum.x, Math.max(0, Math.round(position.x))),
    y: Math.min(maximum.y, Math.max(0, Math.round(position.y))),
  };
}

export function positionForDrag(
  start: FloatingChatDragStart,
  screenX: number,
  screenY: number,
  area: FloatingChatClientArea,
): FloatingChatPosition {
  return clampPosition(
    {
      x: start.x + screenX - start.screenX,
      y: start.y + screenY - start.screenY,
    },
    area,
  );
}

export function dockPosition(
  position: FloatingChatPosition,
  area: FloatingChatClientArea,
): FloatingChatPosition & { side: FloatingChatDockSide } {
  const clamped = clampPosition(position, area);
  const maximum = maximumPosition(area);
  const side = clamped.x <= maximum.x - clamped.x ? "left" : "right";
  return { x: side === "left" ? 0 : maximum.x, y: clamped.y, side };
}

export function positionAfterResize(
  position: FloatingChatPosition,
  area: FloatingChatClientArea,
  side: FloatingChatDockSide,
): FloatingChatPosition {
  const clamped = clampPosition(position, area);
  const maximum = maximumPosition(area);
  return { x: side === "left" ? 0 : maximum.x, y: clamped.y };
}
