"use client";

import * as React from "react";
import { MarkerType } from "reactflow";
import { useShallow } from "zustand/react/shallow";

import type { AgentflowEdge, AgentflowNode, AgentflowPosition } from "../../../../types/agentflow";
import { AGENTFLOW_NODE_SIZE, estimateAgentflowNodeHeight } from "./agentflow-canvas-geometry";
import type { FlowEdge, FlowNode } from "./agentflow-canvas-view";
import { isValidConnection, type NodeReference } from "./agentflow-document";
import { routeAgentflowEdges } from "./agentflow-edge-path";
import { useAgentflowEditorStore } from "./agentflow-editor-store";
import type { AgentflowNodeCardData } from "./agentflow-node-card";
import { getNodeRunStatus } from "./agentflow-run-state";

type CanvasGraphProps = {
  lookups: AgentflowNodeCardData["lookups"];
  positions: ReadonlyMap<string, AgentflowPosition>;
  measuredNodes: FlowNode[];
  connectionSourceNodeId: string | null;
  hoveredEdgeId: string | null;
  actions: {
    onAddMember(nodeId: string, reference: NodeReference): void;
    onDuplicate(nodeIds: string[]): void;
    onDelete(nodeIds: string[]): void;
    onInsert(edgeId: string, position: AgentflowPosition): void;
  };
};

export function useAgentflowCanvasGraph(props: CanvasGraphProps) {
  const state = useAgentflowEditorStore(useShallow(selectCanvasGraph));
  const nodes = useCanvasNodes(state, props);
  const routes = useCanvasRoutes(nodes, state.document, props.measuredNodes);
  const edges = useCanvasEdges(state, props, routes);
  return { nodes, edges, routes };
}

function selectCanvasGraph(state: import("./agentflow-editor-store").AgentflowEditorState) {
  return {
    document: state.document,
    agentflowId: state.agentflowId,
    problems: state.problems.problems,
    selectedNodeIds: state.selectedNodeIds,
    selectedEdgeId: state.selectedEdgeId,
    runState: state.runState,
    selectMember: state.selectMember,
  };
}

type CanvasGraphState = ReturnType<typeof selectCanvasGraph>;

function useCanvasNodes(state: CanvasGraphState, props: CanvasGraphProps) {
  const { document, agentflowId, selectedNodeIds, problems, runState, selectMember } = state;
  const { lookups, positions, connectionSourceNodeId, actions } = props;
  return React.useMemo(() => {
    const problemNodeIds = new Set(problems.map((problem) => problem.nodeId));
    const waitingNodeIds = new Set(
      document.edges.filter((edge) => edge.isFanInBarrier).map((edge) => edge.targetNodeId),
    );
    const context = {
      document,
      agentflowId,
      selectedNodeIds,
      runState,
      selectMember,
      lookups,
      positions,
      connectionSourceNodeId,
      actions,
      problemNodeIds,
      waitingNodeIds,
    };
    return document.nodes.map((node) => createCanvasNode(node, context));
  }, [
    document,
    agentflowId,
    selectedNodeIds,
    problems,
    runState,
    selectMember,
    lookups,
    positions,
    connectionSourceNodeId,
    actions,
  ]);
}

type CanvasNodeContext = Omit<CanvasGraphState, "problems" | "selectedEdgeId"> &
  Pick<CanvasGraphProps, "lookups" | "positions" | "connectionSourceNodeId" | "actions"> & {
    problemNodeIds: ReadonlySet<string | null>;
    waitingNodeIds: ReadonlySet<string>;
  };

function createCanvasNode(node: AgentflowNode, context: CanvasNodeContext): FlowNode {
  const { document, positions, selectedNodeIds, lookups, runState, actions } = context;
  const id = node.nodeId;
  const source = context.connectionSourceNodeId;
  return {
    id,
    type: "dagNode",
    position: positions.get(id)!,
    selected: selectedNodeIds.includes(id),
    data: {
      node,
      lookups,
      runStatus: getNodeRunStatus(runState, id),
      selectedNodeCount: selectedNodeIds.length,
      hasProblem: context.problemNodeIds.has(id),
      isInvalidConnectionTarget: source !== null && !isValidConnection(document, source, id),
      waitsForIncoming: context.waitingNodeIds.has(id),
      activeMemberId: runState.activeMemberIdByNodeId[id] ?? null,
      currentAgentflowId: context.agentflowId,
      agents: lookups.agents,
      agentflows: lookups.agentflows,
      onSelectMember: (nodeId, memberId) => context.selectMember({ nodeId, memberId }),
      onAddMember: (nodeId, _kind, reference) => actions.onAddMember(nodeId, reference),
      onDuplicate: (nodeId) => actions.onDuplicate([nodeId]),
      onDelete: (nodeId) => actions.onDelete([nodeId]),
    },
  };
}

function useCanvasRoutes(
  nodes: FlowNode[],
  document: CanvasGraphState["document"],
  liveNodes: FlowNode[],
) {
  return React.useMemo(() => {
    const measurements = new Map(liveNodes.map((node) => [node.id, node]));
    const layoutNodes = nodes.map(({ id, position, data }) => {
      const measured = measurements.get(id);
      return {
        id,
        ...position,
        width: measured?.width ?? AGENTFLOW_NODE_SIZE.width,
        height: measured?.height ?? estimateAgentflowNodeHeight(data.node),
      };
    });
    return routeAgentflowEdges(layoutNodes, document.edges);
  }, [nodes, document.edges, liveNodes]);
}

function useCanvasEdges(
  state: CanvasGraphState,
  props: CanvasGraphProps,
  routes: ReturnType<typeof useCanvasRoutes>,
) {
  const { document, problems, runState, selectedEdgeId, selectedNodeIds } = state;
  const {
    hoveredEdgeId,
    actions: { onInsert },
  } = props;
  return React.useMemo(() => {
    const problemEdgeIds = new Set(problems.map((problem) => problem.edgeId));
    const context = {
      document,
      runState,
      selectedEdgeId,
      selectedNodeIds,
      hoveredEdgeId,
      routes,
      problemEdgeIds,
      onInsert,
    };
    return document.edges.map((edge) => createCanvasEdge(edge, context));
  }, [
    document,
    problems,
    routes,
    runState,
    selectedEdgeId,
    selectedNodeIds,
    hoveredEdgeId,
    onInsert,
  ]);
}

type CanvasEdgeContext = Pick<
  CanvasGraphState,
  "document" | "runState" | "selectedEdgeId" | "selectedNodeIds"
> & {
  hoveredEdgeId: string | null;
  routes: ReturnType<typeof useCanvasRoutes>;
  problemEdgeIds: ReadonlySet<string | null>;
  onInsert: CanvasGraphProps["actions"]["onInsert"];
};

function createCanvasEdge(edge: AgentflowEdge, context: CanvasEdgeContext): FlowEdge {
  const { document, routes, runState, selectedNodeIds, onInsert } = context;
  const targetStatus = getNodeRunStatus(runState, edge.targetNodeId);
  const sourceStatus = getNodeRunStatus(runState, edge.sourceNodeId);
  const isRunning = targetStatus === "running" && sourceStatus === "completed";
  const hasProblem = context.problemEdgeIds.has(edge.edgeId);
  const selected = edge.edgeId === context.selectedEdgeId;
  const isConnectedToSelectedNode = selectedNodeIds.some(
    (id) => id === edge.sourceNodeId || id === edge.targetNodeId,
  );
  const color = edgeColor({ isRunning, hasProblem, selected, isConnectedToSelectedNode });
  return {
    id: edge.edgeId,
    type: "agentflow",
    source: edge.sourceNodeId,
    target: edge.targetNodeId,
    selected,
    animated: isRunning,
    className: isRunning
      ? "motion-reduce:[&_.react-flow__edge-path]:[animation:none!important]"
      : undefined,
    updatable: "target",
    markerEnd: { type: MarkerType.ArrowClosed, color },
    data: {
      edge,
      nodes: document.nodes,
      edges: document.edges,
      route: routes[edge.edgeId],
      hasProblem,
      isRunning,
      isConnectedToSelectedNode,
      showInsertButton: selected || edge.edgeId === context.hoveredEdgeId,
      onInsert,
    },
  };
}

function edgeColor({
  isRunning,
  hasProblem,
  selected,
  isConnectedToSelectedNode,
}: {
  isRunning: boolean;
  hasProblem: boolean;
  selected: boolean;
  isConnectedToSelectedNode: boolean;
}) {
  if (isRunning) return "var(--agw-status-running)";
  if (hasProblem) return "var(--destructive)";
  if (selected) return "var(--primary)";
  if (isConnectedToSelectedNode) return "var(--foreground)";
  return "var(--agw-agentflow-edge)";
}
