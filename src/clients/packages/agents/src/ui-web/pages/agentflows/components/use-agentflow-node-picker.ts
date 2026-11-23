"use client";

import * as React from "react";
import { useReactFlow } from "reactflow";
import type {
  AgentflowNode,
  AgentflowNodeKind,
  AgentflowPosition,
} from "../../../../types/agentflow";
import {
  AGENTFLOW_NODE_SIZE,
  estimateAgentflowNodeHeight,
  snapAgentflowPosition,
} from "./agentflow-canvas-geometry";
import type { FlowNode } from "./agentflow-canvas-view";
import {
  applyNodePositions,
  createEdge,
  createNode,
  insertNodeOnEdge,
  nodeOverlapsAnother,
  type NodeReference,
} from "./agentflow-document";
import { useAgentflowEditorStoreApi, type AgentflowEditorState } from "./agentflow-editor-store";
import type { useAgentflowArrangement } from "./use-agentflow-arrangement";

type PickerTarget =
  | { kind: "add" }
  | { kind: "insert"; edgeId: string }
  | { kind: "connect"; source: string };

type Request = { target: PickerTarget; position: AgentflowPosition; anchor: AgentflowPosition };
type ArrangeDocument = ReturnType<typeof useAgentflowArrangement>["arrange"];

type NodePickerOptions = {
  canvasRef: React.RefObject<HTMLDivElement | null>;
  liveNodes: FlowNode[];
  positionNode(position: AgentflowPosition): AgentflowPosition;
  arrange: ArrangeDocument;
};

export function useAgentflowNodePicker({
  canvasRef,
  liveNodes,
  positionNode,
  arrange,
}: NodePickerOptions) {
  const store = useAgentflowEditorStoreApi();
  const flow = useReactFlow();
  const [request, setRequest] = React.useState<Request | null>(null);
  const open = React.useCallback(
    (target: PickerTarget, position: AgentflowPosition) => {
      const screen = flow.flowToScreenPosition(position);
      const bounds = canvasRef.current!.getBoundingClientRect();
      setRequest({
        target,
        position,
        anchor: { x: screen.x - bounds.left, y: screen.y - bounds.top },
      });
    },
    [canvasRef, flow],
  );
  const commands = React.useMemo(
    () => ({
      openAdd: (position: AgentflowPosition) => open({ kind: "add" }, position),
      openInsert: (edgeId: string, position: AgentflowPosition) =>
        open({ kind: "insert", edgeId }, position),
      openConnect: (source: string, position: AgentflowPosition) =>
        open({ kind: "connect", source }, position),
      close: () => setRequest(null),
    }),
    [open],
  );
  return {
    ...commands,
    menuOpen: request !== null,
    menuPosition: request?.anchor ?? null,
    onMenuOpenChange: (open: boolean) => {
      if (!open) commands.close();
    },
    onSelectMenuNode: async (kind: AgentflowNodeKind, reference?: NodeReference) => {
      const state = store.getState();
      if (!request || state.isSaving) return;
      const { target, position } = request;
      const node = createNode(
        kind,
        target.kind === "insert" ? snapAgentflowPosition(position) : positionNode(position),
        reference,
      );
      await addPickedNode(state, target, node, { liveNodes, arrange });
      state.selectNodes([node.nodeId]);
      commands.close();
    },
  };
}

async function addPickedNode(
  state: AgentflowEditorState,
  target: PickerTarget,
  node: AgentflowNode,
  layout: Pick<NodePickerOptions, "liveNodes" | "arrange">,
) {
  if (target.kind === "insert") {
    await insertPickedNode(state, target.edgeId, node, layout);
    return;
  }
  state.updateDocument((document) => {
    const next = { ...document, nodes: [...document.nodes, node] };
    if (target.kind === "add") return next;
    const edge = createEdge(next, target.source, node.nodeId);
    return { ...next, edges: [...next.edges, edge] };
  });
}

async function insertPickedNode(
  state: AgentflowEditorState,
  edgeId: string,
  node: AgentflowNode,
  { liveNodes, arrange }: Pick<NodePickerOptions, "liveNodes" | "arrange">,
) {
  const candidate = insertNodeOnEdge(state.document, edgeId, node);
  const sizes = measuredSizes(liveNodes, node);
  if (!nodeOverlapsAnother(candidate, node.nodeId, sizes)) {
    state.updateDocument((document) => insertNodeOnEdge(document, edgeId, node));
    return;
  }
  const arranged = await arrange(candidate, false);
  const positions = Object.fromEntries(
    arranged.nodes.map(({ nodeId, position }) => [
      nodeId,
      { x: Number(position.x), y: Number(position.y) },
    ]),
  );
  state.updateDocument((document) =>
    applyNodePositions(insertNodeOnEdge(document, edgeId, node), positions),
  );
}

function measuredSizes(liveNodes: FlowNode[], node: AgentflowNode) {
  return {
    ...Object.fromEntries(
      liveNodes.map(({ id, width, height }) => [
        id,
        {
          width: width ?? AGENTFLOW_NODE_SIZE.width,
          height: height ?? AGENTFLOW_NODE_SIZE.height,
        },
      ]),
    ),
    [node.nodeId]: { width: AGENTFLOW_NODE_SIZE.width, height: estimateAgentflowNodeHeight(node) },
  };
}
