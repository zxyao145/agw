import type { AgentflowNode, AgentflowPosition } from "../../../../types/agentflow";

export const AGENTFLOW_GRID_SIZE = 20;
export const AGENTFLOW_NODE_SIZE = { width: 240, height: 64 } as const;
export const AGENTFLOW_NODE_MEMBER_BASE_HEIGHT = 92;
export const AGENTFLOW_NODE_MEMBER_ROW_HEIGHT = 30;
export const AGENTFLOW_EDGE_LABEL_SIZE = { width: 200, height: 24 } as const;

export type AgentflowCanvasViewport = { x: number; y: number; zoom: number };
export type AgentflowNodeSize = { width: number; height: number };

export function snapAgentflowPosition(position: AgentflowPosition): AgentflowPosition {
  return {
    x: Math.round(position.x / AGENTFLOW_GRID_SIZE) * AGENTFLOW_GRID_SIZE,
    y: Math.round(position.y / AGENTFLOW_GRID_SIZE) * AGENTFLOW_GRID_SIZE,
  };
}

export function estimateAgentflowNodeHeight(node: AgentflowNode | undefined): number {
  return node && "members" in node
    ? AGENTFLOW_NODE_MEMBER_BASE_HEIGHT + node.members.length * AGENTFLOW_NODE_MEMBER_ROW_HEIGHT
    : AGENTFLOW_NODE_SIZE.height;
}

export function isCanvasRectVisible(
  rect: { x: number; y: number; width: number; height: number },
  viewport: AgentflowCanvasViewport,
  canvasSize: { width: number; height: number },
  margin = 24,
): boolean {
  const left = viewport.x + rect.x * viewport.zoom;
  const top = viewport.y + rect.y * viewport.zoom;
  const right = left + rect.width * viewport.zoom;
  const bottom = top + rect.height * viewport.zoom;
  return (
    left >= margin &&
    top >= margin &&
    right <= canvasSize.width - margin &&
    bottom <= canvasSize.height - margin
  );
}
