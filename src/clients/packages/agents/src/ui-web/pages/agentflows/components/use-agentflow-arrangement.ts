"use client";

import * as React from "react";
import { AGENTFLOW_NODE_SIZE, estimateAgentflowNodeHeight } from "./agentflow-canvas-geometry";
import type { FlowNode } from "./agentflow-canvas-view";
import { applyNodePositions, type AgentflowEditorDocument } from "./agentflow-document";
import { useAgentflowEditorStoreApi } from "./agentflow-editor-store";
import { createGraphLayout } from "./auto-layout";

type CanvasRef = React.RefObject<HTMLDivElement | null>;
type NodeMeasurement = Pick<FlowNode, "id" | "width" | "height">;

export function useAgentflowArrangement(canvasRef: CanvasRef, liveNodes: NodeMeasurement[]) {
  const store = useAgentflowEditorStoreApi();
  const canvasSize = useCanvasSize(canvasRef);
  const arrange = React.useCallback(
    async (sourceDocument?: AgentflowEditorDocument, recordHistory = true) => {
      const { document, isSaving, updateDocument } = store.getState();
      const source = sourceDocument ?? document;
      if (recordHistory && isSaving) return source;
      const sizes = readNodeSizes(source, liveNodes);
      const positions = await createGraphLayout(source, sizes, canvasSize);
      if (recordHistory) updateDocument((current) => applyNodePositions(current, positions));
      return applyNodePositions(source, positions);
    },
    [canvasSize, liveNodes, store],
  );
  return { arrange, canvasSize };
}

function useCanvasSize(canvasRef: CanvasRef) {
  const [canvasSize, setCanvasSize] = React.useState({ width: 800, height: 500 });
  React.useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const observer = new ResizeObserver(() =>
      setCanvasSize({ width: canvas.clientWidth, height: canvas.clientHeight }),
    );
    observer.observe(canvas);
    return () => observer.disconnect();
  }, [canvasRef]);
  return canvasSize;
}

function readNodeSizes(document: AgentflowEditorDocument, liveNodes: NodeMeasurement[]) {
  const measured = new Map(liveNodes.map((node) => [node.id, node]));
  return Object.fromEntries(
    document.nodes.map((node) => {
      const size = measured.get(node.nodeId);
      return [
        node.nodeId,
        {
          width: size?.width ?? AGENTFLOW_NODE_SIZE.width,
          height: size?.height ?? estimateAgentflowNodeHeight(node),
        },
      ];
    }),
  );
}
