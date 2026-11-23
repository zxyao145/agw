"use client";

import * as React from "react";
import { EdgeLabelRenderer, BaseEdge, type EdgeProps, getSmoothStepPath } from "reactflow";
import { Plus } from "lucide-react";
import { Button } from "@agw/components";

import type {
  AgentflowEdge as AgentflowEdgeDto,
  AgentflowNode,
  AgentflowPosition,
} from "../../../../types/agentflow";
import type { EdgeRoute } from "./agentflow-edge-path";
import { describeAgentflowEdge } from "./agentflow-condition";
import { AGENTFLOW_EDGE_LABEL_SIZE } from "./agentflow-canvas-geometry";

export type AgentflowEdgeData = {
  edge: AgentflowEdgeDto;
  nodes: AgentflowNode[];
  edges: AgentflowEdgeDto[];
  route: EdgeRoute;
  hasProblem: boolean;
  isRunning: boolean;
  isConnectedToSelectedNode: boolean;
  showInsertButton: boolean;
  onInsert(edgeId: string, position: AgentflowPosition): void;
};

export function AgentflowEdge({
  id,
  data,
  markerEnd,
  selected,
  sourceX,
  sourceY,
  targetX,
  targetY,
}: EdgeProps<AgentflowEdgeData>) {
  if (!data) return null;
  const edge = data.edge;
  const autoLabel = describeAgentflowEdge(edge, data.nodes, data.edges);
  const label = edge.label || autoLabel;
  const route = data.route;
  const color = data.isRunning
    ? "var(--agw-status-running)"
    : data.hasProblem
      ? "var(--destructive)"
      : selected
        ? "var(--primary)"
        : data.isConnectedToSelectedNode
          ? "var(--foreground)"
          : "var(--agw-agentflow-edge)";
  const strokeWidth = data.isRunning ? 2 : data.hasProblem ? 1.5 : selected ? 2.25 : 1.5;
  const path = route?.path ?? getSmoothStepPath({ sourceX, sourceY, targetX, targetY })[0];
  const labelPosition = route?.label ?? { x: (sourceX + targetX) / 2, y: (sourceY + targetY) / 2 };

  return (
    <>
      <BaseEdge id={id} path={path} markerEnd={markerEnd} style={{ stroke: color, strokeWidth }} />
      {label || data.showInsertButton ? (
        <EdgeLabelRenderer>
          <div
            className="nodrag nopan pointer-events-none absolute flex items-center gap-1"
            style={{
              transform: `translate(-50%, -50%) translate(${labelPosition.x}px, ${labelPosition.y}px)`,
            }}
          >
            {label ? (
              <span
                className="truncate rounded-md border border-border bg-card px-2 py-0.5 text-[11px] text-muted-foreground"
                style={{ maxWidth: AGENTFLOW_EDGE_LABEL_SIZE.width }}
                title={label}
              >
                {label}
              </span>
            ) : null}
            {data.showInsertButton ? (
              <Button
                type="button"
                size="icon-sm"
                variant="secondary"
                className="pointer-events-auto h-5 w-5 rounded-full p-0"
                onClick={() => data.onInsert(edge.edgeId, labelPosition)}
                aria-label="Insert a node here"
                title="Insert a node here"
              >
                <Plus className="h-3 w-3" />
              </Button>
            ) : null}
          </div>
        </EdgeLabelRenderer>
      ) : null}
    </>
  );
}
