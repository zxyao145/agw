"use client";

import * as React from "react";
import { useReactFlow, type Connection, type OnConnectStartParams } from "reactflow";
import type { AgentflowPosition } from "../../../../types/agentflow";
import type { FlowEdge } from "./agentflow-canvas-view";
import { createEdge, isValidConnection, retargetEdge } from "./agentflow-document";
import { useAgentflowEditorStoreApi, type AgentflowEditorState } from "./agentflow-editor-store";

export function useAgentflowConnections(
  openTarget: (source: string, point: AgentflowPosition) => void,
) {
  const store = useAgentflowEditorStoreApi();
  const flow = useReactFlow();
  const activeSource = React.useRef<string | null>(null);
  const [connectionSourceNodeId, setSource] = React.useState<string | null>(null);
  const recordSource = (source: string | null) => {
    activeSource.current = source;
    setSource(source);
  };
  return {
    connectionSourceNodeId,
    isValidConnection: (connection: Connection) => isConnectable(store.getState(), connection),
    onConnect: (connection: Connection) => {
      if (appendConnection(store.getState(), connection)) recordSource(null);
    },
    onConnectStart: (_event: React.MouseEvent | React.TouchEvent, params: OnConnectStartParams) =>
      recordSource(params.nodeId),
    onConnectEnd: (event: MouseEvent | TouchEvent) => {
      const source = activeSource.current;
      const target = event.target;
      if (source && target instanceof HTMLElement && target.closest(".react-flow__pane")) {
        openTarget(source, flow.screenToFlowPosition(getPointerPosition(event)));
      }
      recordSource(null);
    },
    onEdgeUpdate: (edge: FlowEdge, connection: Connection) =>
      updateConnection(store.getState(), edge, connection),
  };
}

function isConnectable({ document }: AgentflowEditorState, { source, target }: Connection) {
  return Boolean(source && target && isValidConnection(document, source, target));
}

function appendConnection(
  { document, updateDocument }: AgentflowEditorState,
  { source, target }: Connection,
): boolean {
  if (!source || !target || !isValidConnection(document, source, target)) return false;
  const edge = createEdge(document, source, target);
  updateDocument((current) => ({ ...current, edges: [...current.edges, edge] }));
  return true;
}

function updateConnection(state: AgentflowEditorState, edge: FlowEdge, { target }: Connection) {
  if (target && isValidConnection(state.document, edge.source, target)) {
    state.updateDocument((current) => retargetEdge(current, edge.id, target));
  }
}

function getPointerPosition(event: MouseEvent | TouchEvent): AgentflowPosition {
  return "touches" in event
    ? { x: event.changedTouches[0].clientX, y: event.changedTouches[0].clientY }
    : { x: event.clientX, y: event.clientY };
}
