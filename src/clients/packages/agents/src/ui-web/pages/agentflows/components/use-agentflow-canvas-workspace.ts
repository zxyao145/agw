"use client";

import * as React from "react";
import { useNodes } from "reactflow";
import type { FlowNode } from "./agentflow-canvas-view";
import { searchNodes } from "./agentflow-document";
import { useAgentflowEditorStore } from "./agentflow-editor-store";
import type { AgentflowNodeCardData } from "./agentflow-node-card";
import { useAgentflowArrangement } from "./use-agentflow-arrangement";
import type { useAgentflowCanvasCommands } from "./use-agentflow-canvas-commands";
import { useAgentflowCanvasGraph } from "./use-agentflow-canvas-graph";
import { useAgentflowCanvasNavigation } from "./use-agentflow-canvas-navigation";
import type { useAgentflowNodePicker } from "./use-agentflow-node-picker";

export function useAgentflowCanvasWorkspace() {
  const document = useAgentflowEditorStore((state) => state.document);
  const currentAgentflowId = useAgentflowEditorStore((state) => state.agentflowId);
  const liveNodes = useNodes<FlowNode["data"]>();
  const canvasRef = React.useRef<HTMLDivElement | null>(null);
  const searchInputRef = React.useRef<HTMLInputElement | null>(null);
  const [searchText, onSearchTextChange] = React.useState("");
  const { arrange, canvasSize } = useAgentflowArrangement(canvasRef, liveNodes);
  const clearSearch = React.useCallback(() => onSearchTextChange(""), []);
  const navigation = useAgentflowCanvasNavigation(canvasRef, canvasSize, liveNodes, clearSearch);
  const emptyCanvas =
    !currentAgentflowId && document.nodes.length === 1 && document.nodes[0].kind === "input";
  const view = {
    canvasRef,
    searchInputRef,
    searchText,
    onSearchTextChange,
    currentAgentflowId,
    emptyCanvas,
    searchResults: searchNodes(document, searchText),
    onSearchResult: navigation.showSearchResult,
    onTidy: () => void arrange(),
  };
  return { view, navigation, liveNodes, arrange, clearSearch };
}

type Workspace = ReturnType<typeof useAgentflowCanvasWorkspace>;

export function useAgentflowCanvasDisplay(
  workspace: Workspace,
  lookups: AgentflowNodeCardData["lookups"],
  commands: ReturnType<typeof useAgentflowCanvasCommands>,
  onInsert: ReturnType<typeof useAgentflowNodePicker>["openInsert"],
  connectionSourceNodeId: string | null,
) {
  const [hoveredEdgeId, setHoveredEdgeId] = React.useState<string | null>(null);
  const actions = React.useMemo(
    () => ({
      onAddMember: commands.addNodeMember,
      onDuplicate: commands.duplicate,
      onDelete: commands.removeNodes,
      onInsert,
    }),
    [commands, onInsert],
  );
  const { nodes, edges, routes } = useAgentflowCanvasGraph({
    lookups,
    actions,
    positions: workspace.navigation.positions,
    measuredNodes: workspace.liveNodes,
    connectionSourceNodeId,
    hoveredEdgeId,
  });
  const showEdge = (edgeId: string) => {
    const label = routes[edgeId]?.label;
    if (label) workspace.navigation.focusEdgeLabel(label);
  };
  const view = {
    nodes,
    edges,
    onEdgeMouseEnter: setHoveredEdgeId,
    onEdgeMouseLeave: () => setHoveredEdgeId(null),
  };
  return { view, showEdge };
}
