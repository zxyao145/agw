"use client";

import * as React from "react";
import { useReactFlow } from "reactflow";

import type { AgentflowNodeKind, AgentflowPosition } from "../../../../types/agentflow";
import { useAgentflowEditorStore } from "./agentflow-editor-store";
import type { FlowEdge, FlowNode } from "./agentflow-canvas-view";
import { type NodeReference } from "./agentflow-document";
import { AGENTFLOW_NODE_DRAG_TYPE } from "./agentflow-node-library";
import { snapAgentflowPosition } from "./agentflow-canvas-geometry";

type AddNode = (
  kind: AgentflowNodeKind,
  reference?: NodeReference,
  position?: AgentflowPosition,
) => void;

export function useAgentflowCanvasInteractions({
  addNode,
  closeMenu,
}: {
  addNode: AddNode;
  closeMenu(): void;
}) {
  const selectedNodeIds = useAgentflowEditorStore((state) => state.selectedNodeIds);
  const selectedEdgeId = useAgentflowEditorStore((state) => state.selectedEdgeId);
  const selectNodes = useAgentflowEditorStore((state) => state.selectNodes);
  const selectEdge = useAgentflowEditorStore((state) => state.selectEdge);
  const clearSelection = useAgentflowEditorStore((state) => state.clearSelection);
  const updateDocument = useAgentflowEditorStore((state) => state.updateDocument);
  const commitHistoryGroup = useAgentflowEditorStore((state) => state.commitHistoryGroup);
  const flow = useReactFlow<FlowNode, FlowEdge>();
  const isMarqueeSelecting = React.useRef(false);

  const onNodesChange = React.useCallback(
    (changes: import("reactflow").NodeChange[]) => {
      const selectionChanges = changes.filter(
        (change): change is import("reactflow").NodeSelectionChange => change.type === "select",
      );
      if (isMarqueeSelecting.current && selectionChanges.length) {
        const selected = new Set(selectedNodeIds);
        for (const change of selectionChanges) {
          if (change.selected) selected.add(change.id);
          else selected.delete(change.id);
        }
        selectNodes([...selected]);
      }

      const positionChanges = changes.filter(
        (
          change,
        ): change is import("reactflow").NodeChange & {
          type: "position";
          position: { x: number; y: number };
        } => change.type === "position" && Boolean(change.position),
      );
      if (positionChanges.length) {
        const positions = new Map(
          positionChanges.map((change) => [change.id, snapAgentflowPosition(change.position)]),
        );
        updateDocument(
          (current) => ({
            ...current,
            nodes: current.nodes.map((node) =>
              positions.has(node.nodeId)
                ? { ...node, position: positions.get(node.nodeId)! }
                : node,
            ),
          }),
          { group: "move-nodes" },
        );
      }
    },
    [selectedNodeIds, selectNodes, updateDocument],
  );

  const onEdgesChange = React.useCallback(
    (changes: import("reactflow").EdgeChange[]) => {
      if (isMarqueeSelecting.current) return;
      const selectionChanges = changes.filter(
        (change): change is import("reactflow").EdgeSelectionChange => change.type === "select",
      );
      const selectedChange = selectionChanges.find((change) => change.selected);
      if (selectedChange) selectEdge(selectedChange.id);
      else if (selectionChanges.some((change) => change.id === selectedEdgeId)) selectEdge(null);
    },
    [selectedEdgeId, selectEdge],
  );

  const onNodeClick = React.useCallback(
    (event: React.MouseEvent, node: FlowNode) => {
      if (event.metaKey || event.ctrlKey) {
        selectNodes(
          selectedNodeIds.includes(node.id)
            ? selectedNodeIds.filter((nodeId) => nodeId !== node.id)
            : [...selectedNodeIds, node.id],
        );
      } else {
        selectNodes([node.id]);
      }
    },
    [selectedNodeIds, selectNodes],
  );

  const onPaneClick = React.useCallback(() => {
    clearSelection();
    closeMenu();
  }, [clearSelection, closeMenu]);

  const onDrop = React.useCallback(
    (event: React.DragEvent<HTMLDivElement>) => {
      event.preventDefault();
      const value = event.dataTransfer.getData(AGENTFLOW_NODE_DRAG_TYPE);
      if (!value) return;
      const payload = JSON.parse(value) as { kind: AgentflowNodeKind; reference?: NodeReference };
      addNode(
        payload.kind,
        payload.reference,
        flow.screenToFlowPosition({ x: event.clientX, y: event.clientY }),
      );
    },
    [addNode, flow],
  );

  const onSelectionStart = React.useCallback(() => {
    isMarqueeSelecting.current = true;
    clearSelection();
  }, [clearSelection]);
  const onSelectionEnd = React.useCallback(() => {
    isMarqueeSelecting.current = false;
  }, []);

  return {
    onNodesChange,
    onEdgesChange,
    onNodeClick,
    onPaneClick,
    onDrop,
    onSelectionStart,
    onSelectionEnd,
    onNodeDragStop: commitHistoryGroup,
  };
}
