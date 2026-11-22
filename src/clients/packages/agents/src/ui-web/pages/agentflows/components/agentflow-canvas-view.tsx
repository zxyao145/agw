"use client";

import * as React from "react";
import ReactFlow, {
  Background,
  BackgroundVariant,
  Edge,
  MiniMap,
  Node,
  SelectionMode,
  useReactFlow,
  useViewport,
} from "reactflow";
import { Button, Input } from "@agw/components";
import { LocateFixed, ZoomIn, ZoomOut } from "lucide-react";

import type {
  AgentDto,
  AgentflowDto,
  AgentflowNode,
  AgentflowNodeKind,
  AgentflowPosition,
} from "../../../../types/agentflow";
import { AgentflowEdge, type AgentflowEdgeData } from "./agentflow-edge";
import { AgentflowNodeMenu } from "./agentflow-node-menu";
import { AGENTFLOW_NODE_META } from "./agentflow-node-meta";
import { AGENTFLOW_TEMPLATES } from "./agentflow-templates";
import { AgentflowNodeCard, type AgentflowNodeCardData } from "./agentflow-node-card";
import { AGENTFLOW_GRID_SIZE } from "./agentflow-canvas-geometry";
import type { NodeReference } from "./agentflow-document";

export type FlowNode = Node<AgentflowNodeCardData>;
export type FlowEdge = Edge<AgentflowEdgeData>;

const nodeTypes = { dagNode: AgentflowNodeCard };
const edgeTypes = { agentflow: AgentflowEdge };

export type AgentflowCanvasViewProps = {
  nodes: FlowNode[];
  edges: FlowEdge[];
  canvasRef: React.RefObject<HTMLDivElement | null>;
  searchInputRef: React.RefObject<HTMLInputElement | null>;
  searchText: string;
  searchResults: AgentflowNode[];
  agents: AgentDto[];
  agentflows: AgentflowDto[];
  currentAgentflowId: string | null;
  emptyCanvas: boolean;
  menuOpen: boolean;
  menuPosition: AgentflowPosition | null;
  onSearchTextChange(searchText: string): void;
  onDrop(event: React.DragEvent<HTMLDivElement>): void;
  onConnect(connection: import("reactflow").Connection): void;
  onConnectStart(
    event: React.MouseEvent | React.TouchEvent,
    params: import("reactflow").OnConnectStartParams,
  ): void;
  onConnectEnd(event: MouseEvent | TouchEvent): void;
  isValidConnection(connection: import("reactflow").Connection): boolean;
  onNodesChange(changes: import("reactflow").NodeChange[]): void;
  onEdgesChange(changes: import("reactflow").EdgeChange[]): void;
  onNodeClick(event: React.MouseEvent, node: FlowNode): void;
  onSelectionStart(): void;
  onSelectionEnd(): void;
  onPaneClick(): void;
  onNodeContextMenu(event: React.MouseEvent, node: FlowNode): void;
  onEdgeContextMenu(event: React.MouseEvent, edge: FlowEdge): void;
  onPaneContextMenu(event: React.MouseEvent): void;
  onEdgeMouseEnter(edgeId: string): void;
  onEdgeMouseLeave(): void;
  onNodeDragStop(): void;
  onEdgeUpdate(oldEdge: FlowEdge, connection: import("reactflow").Connection): void;
  onSearchResult(nodeId: string): void;
  onTidy(): void;
  onUseTemplate(templateId: string): void;
  onMenuOpenChange(open: boolean): void;
  onSelectMenuNode(kind: AgentflowNodeKind, reference?: NodeReference): void;
};

export function AgentflowCanvasView({
  nodes,
  edges,
  canvasRef,
  searchInputRef,
  searchText,
  searchResults,
  agents,
  agentflows,
  currentAgentflowId,
  emptyCanvas,
  menuOpen,
  menuPosition,
  onSearchTextChange,
  onDrop,
  onConnect,
  onConnectStart,
  onConnectEnd,
  isValidConnection,
  onNodesChange,
  onEdgesChange,
  onNodeClick,
  onSelectionStart,
  onSelectionEnd,
  onPaneClick,
  onNodeContextMenu,
  onEdgeContextMenu,
  onPaneContextMenu,
  onEdgeMouseEnter,
  onEdgeMouseLeave,
  onNodeDragStop,
  onEdgeUpdate,
  onSearchResult,
  onTidy,
  onUseTemplate,
  onMenuOpenChange,
  onSelectMenuNode,
}: AgentflowCanvasViewProps) {
  const flow = useReactFlow<FlowNode, FlowEdge>();
  const viewport = useViewport();

  return (
    <div
      className="relative h-full min-h-0"
      ref={canvasRef}
      onDrop={onDrop}
      onDragOver={(event) => event.preventDefault()}
    >
      <ReactFlow
        nodes={nodes}
        edges={edges}
        nodeTypes={nodeTypes}
        edgeTypes={edgeTypes}
        style={{ overflow: "clip" }}
        onConnect={onConnect}
        onConnectStart={onConnectStart}
        onConnectEnd={onConnectEnd}
        isValidConnection={isValidConnection}
        onNodesChange={onNodesChange}
        onEdgesChange={onEdgesChange}
        onNodeClick={onNodeClick}
        onSelectionStart={onSelectionStart}
        onSelectionEnd={onSelectionEnd}
        onPaneClick={onPaneClick}
        onNodeContextMenu={onNodeContextMenu}
        onEdgeContextMenu={onEdgeContextMenu}
        onPaneContextMenu={onPaneContextMenu}
        onEdgeMouseEnter={(_, edge) => onEdgeMouseEnter(edge.id)}
        onEdgeMouseLeave={() => onEdgeMouseLeave()}
        onNodeDragStop={onNodeDragStop}
        onEdgeUpdate={onEdgeUpdate}
        snapToGrid
        snapGrid={[AGENTFLOW_GRID_SIZE, AGENTFLOW_GRID_SIZE]}
        fitView
        fitViewOptions={{ padding: 0.2, maxZoom: 1 }}
        proOptions={{ hideAttribution: true }}
        selectionOnDrag
        selectNodesOnDrag={false}
        selectionMode={SelectionMode.Partial}
        selectionKeyCode="Shift"
        multiSelectionKeyCode={["Meta", "Control"]}
        deleteKeyCode={null}
        nodesConnectable
        edgesUpdatable
        defaultEdgeOptions={{ type: "agentflow" }}
      >
        <Background
          variant={BackgroundVariant.Dots}
          gap={AGENTFLOW_GRID_SIZE}
          size={1}
          color="var(--border)"
        />
        <MiniMap
          pannable
          zoomable
          nodeColor={(node) => {
            const data = node.data as AgentflowNodeCardData;
            if (node.selected) return "var(--primary)";
            if (data.runStatus === "running") return "var(--agw-status-running)";
            if (data.hasProblem) return "var(--destructive)";
            return "var(--border)";
          }}
          maskColor="color-mix(in srgb, var(--background) 70%, transparent)"
          className="border !bg-card !border-border"
        />
        <div className="absolute right-3 top-3 z-10 flex items-start gap-2">
          <div className="relative w-64">
            <Input
              ref={searchInputRef}
              value={searchText}
              onChange={(event) => onSearchTextChange(event.target.value)}
              onKeyDown={(event) => {
                if (event.key !== "Enter" || !searchResults.length) return;
                event.preventDefault();
                onSearchResult(searchResults[0].nodeId);
              }}
              placeholder="Find a node"
              aria-label="Find a node"
              className="bg-card"
            />
            {searchText ? (
              <div className="absolute top-full z-20 mt-1 w-full rounded-md border bg-popover p-1 shadow-md">
                {searchResults.map((node) => (
                  <button
                    key={node.nodeId}
                    type="button"
                    className="block w-full truncate rounded px-2 py-1.5 text-left text-sm hover:bg-muted"
                    onClick={() => onSearchResult(node.nodeId)}
                  >
                    {node.name ?? AGENTFLOW_NODE_META[node.kind].label}{" "}
                    <span className="text-xs text-muted-foreground">
                      {AGENTFLOW_NODE_META[node.kind].label}
                    </span>
                  </button>
                ))}
                {!searchResults.length ? (
                  <p className="px-2 py-1.5 text-xs text-muted-foreground">No nodes found.</p>
                ) : null}
              </div>
            ) : null}
          </div>
        </div>
        <div className="absolute bottom-3 left-3 z-10 flex items-center gap-1 rounded-md border bg-card p-1 shadow-sm">
          <Button
            type="button"
            size="icon-sm"
            variant="ghost"
            aria-label="Zoom out"
            onClick={() => flow.zoomOut()}
          >
            <ZoomOut className="h-4 w-4" />
          </Button>
          <span className="min-w-12 text-center text-xs tabular-nums">
            {Math.round(viewport.zoom * 100)}%
          </span>
          <Button
            type="button"
            size="icon-sm"
            variant="ghost"
            aria-label="Zoom in"
            onClick={() => flow.zoomIn()}
          >
            <ZoomIn className="h-4 w-4" />
          </Button>
          <Button
            type="button"
            size="icon-sm"
            variant="ghost"
            aria-label="Fit view"
            onClick={() => flow.fitView({ padding: 0.2, maxZoom: 1 })}
          >
            <LocateFixed className="h-4 w-4" />
          </Button>
          <Button type="button" size="sm" variant="ghost" onClick={onTidy}>
            Tidy up
          </Button>
        </div>
        {emptyCanvas ? (
          <div className="pointer-events-none absolute inset-0 z-20 flex items-center justify-center">
            <div className="pointer-events-auto max-w-md rounded-xl border bg-card/95 p-5 text-center shadow-sm">
              <h2 className="text-sm font-semibold">Start from a template</h2>
              <div className="mt-3 flex flex-wrap justify-center gap-2">
                {AGENTFLOW_TEMPLATES.map((template) => (
                  <Button
                    key={template.id}
                    type="button"
                    variant="outline"
                    size="sm"
                    onClick={() => onUseTemplate(template.id)}
                  >
                    {template.name}
                  </Button>
                ))}
              </div>
            </div>
          </div>
        ) : null}
        {menuOpen && menuPosition ? (
          <AgentflowNodeMenu
            agents={agents}
            agentflows={agentflows}
            currentAgentflowId={currentAgentflowId}
            open={menuOpen}
            onOpenChange={onMenuOpenChange}
            anchorStyle={{ left: menuPosition.x, top: menuPosition.y }}
            onSelect={onSelectMenuNode}
          />
        ) : null}
      </ReactFlow>
    </div>
  );
}
