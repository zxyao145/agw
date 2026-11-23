"use client";

import * as React from "react";
import { useReactFlow, useViewport } from "reactflow";
import type { AgentflowPosition } from "../../../../types/agentflow";
import {
  AGENTFLOW_EDGE_LABEL_SIZE,
  AGENTFLOW_GRID_SIZE,
  AGENTFLOW_NODE_SIZE,
  estimateAgentflowNodeHeight,
  isCanvasRectVisible,
  snapAgentflowPosition,
} from "./agentflow-canvas-geometry";
import type { FlowNode } from "./agentflow-canvas-view";
import { useAgentflowEditorStore } from "./agentflow-editor-store";

type CanvasSize = { width: number; height: number };
type CanvasRect = AgentflowPosition & CanvasSize;

export function useAgentflowCanvasNavigation(
  canvasRef: React.RefObject<HTMLDivElement | null>,
  canvasSize: CanvasSize,
  liveNodes: FlowNode[],
  onShowNode: () => void,
) {
  const nodes = useAgentflowEditorStore((state) => state.document.nodes);
  const selectNodes = useAgentflowEditorStore((state) => state.selectNodes);
  const flow = useReactFlow();
  const viewport = useViewport();
  const positions = React.useMemo(
    () =>
      new Map(
        nodes.map(({ nodeId, position }) => [
          nodeId,
          { x: Number(position.x), y: Number(position.y) },
        ]),
      ),
    [nodes],
  );
  const positionNode = React.useCallback(
    (position?: AgentflowPosition) => {
      const requested =
        position ?? flow.screenToFlowPosition(center(canvasRef.current!.getBoundingClientRect()));
      return findOpenPosition(requested, positions);
    },
    [canvasRef, flow, positions],
  );
  const focus = React.useCallback(
    (rect: CanvasRect) => {
      if (isCanvasRectVisible(rect, viewport, canvasSize)) return;
      const target = center(rect);
      flow.setCenter(target.x, target.y, { duration: 250, zoom: viewport.zoom });
    },
    [flow, viewport, canvasSize],
  );
  const showSearchResult = React.useCallback(
    (id: string) => {
      const node = nodes.find((item) => item.nodeId === id);
      if (!node) return;
      const measured = liveNodes.find((item) => item.id === id);
      selectNodes([id]);
      focus({
        ...positions.get(id)!,
        width: measured?.width ?? AGENTFLOW_NODE_SIZE.width,
        height: measured?.height ?? estimateAgentflowNodeHeight(node),
      });
      onShowNode();
    },
    [nodes, liveNodes, positions, selectNodes, focus, onShowNode],
  );
  return {
    positions,
    positionNode,
    showSearchResult,
    focusEdgeLabel: (label: AgentflowPosition) => focus(labelBounds(label)),
  };
}

function center({ x, y, width, height }: CanvasRect): AgentflowPosition {
  return { x: x + width / 2, y: y + height / 2 };
}

function findOpenPosition(
  position: AgentflowPosition,
  positions: ReadonlyMap<string, AgentflowPosition>,
) {
  let candidate = snapAgentflowPosition(position);
  const occupied = [...positions.values()];
  while (
    occupied.some(
      (node) =>
        Math.abs(node.x - candidate.x) < AGENTFLOW_NODE_SIZE.width &&
        Math.abs(node.y - candidate.y) < AGENTFLOW_NODE_SIZE.height + 36,
    )
  ) {
    candidate = {
      x: candidate.x + AGENTFLOW_GRID_SIZE * 2,
      y: candidate.y + AGENTFLOW_GRID_SIZE * 2,
    };
  }
  return candidate;
}

function labelBounds({ x, y }: AgentflowPosition): CanvasRect {
  return {
    x: x - AGENTFLOW_EDGE_LABEL_SIZE.width / 2,
    y: y - AGENTFLOW_EDGE_LABEL_SIZE.height / 2,
    ...AGENTFLOW_EDGE_LABEL_SIZE,
  };
}
