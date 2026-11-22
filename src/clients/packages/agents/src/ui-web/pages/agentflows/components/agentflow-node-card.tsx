"use client";

import * as React from "react";
import {
  Bot,
  Check,
  CircleAlert,
  CircleX,
  Crown,
  Hand,
  LoaderCircle,
  Plus,
  Trash2,
} from "lucide-react";
import { Handle, NodeToolbar, Position, type NodeProps } from "reactflow";
import { Button } from "@agw/components";
import type { AgentflowNodeStatus } from "@agw/execution-core";

import type { AgentflowNode, AgentflowNodeKind } from "../../../../types/agentflow";
import type { AgentflowNodeLookups } from "./agentflow-node-meta";
import { AGENTFLOW_NODE_META, describeNode } from "./agentflow-node-meta";
import { AgentflowNodeMenu } from "./agentflow-node-menu";
import type { NodeReference } from "./agentflow-document";
import { AGENTFLOW_NODE_SIZE } from "./agentflow-canvas-geometry";

export type AgentflowNodeCardData = {
  node: AgentflowNode;
  lookups: AgentflowNodeLookups;
  runStatus: AgentflowNodeStatus | null;
  selectedNodeCount: number;
  hasProblem: boolean;
  isInvalidConnectionTarget: boolean;
  waitsForIncoming: boolean;
  activeMemberId: string | null;
  currentAgentflowId: string | null;
  agents: AgentflowNodeMenuProps["agents"];
  agentflows: AgentflowNodeMenuProps["agentflows"];
  onSelectMember(nodeId: string, memberId: string): void;
  onAddMember(nodeId: string, kind: AgentflowNodeKind, reference: NodeReference): void;
  onDuplicate(nodeId: string): void;
  onDelete(nodeId: string): void;
};

type AgentflowNodeMenuProps = React.ComponentProps<typeof AgentflowNodeMenu>;

export function AgentflowNodeCard({ id, data, selected }: NodeProps<AgentflowNodeCardData>) {
  const node = data.node;
  const meta = AGENTFLOW_NODE_META[node.kind];
  const Icon = meta.icon;
  const runStatus = data.runStatus;
  const hasMembers = "members" in node;
  const canDelete = node.kind !== "input";
  const borderStyle = getStatusStyle(runStatus);

  return (
    <div
      className={`relative overflow-visible rounded-lg border bg-card text-card-foreground ${selected ? "border-primary ring-1 ring-primary" : "border-border"} ${data.isInvalidConnectionTarget ? "opacity-50" : ""}`}
      style={{ width: AGENTFLOW_NODE_SIZE.width, ...(selected ? {} : (borderStyle ?? {})) }}
    >
      {node.kind !== "input" ? (
        <Handle
          type="target"
          position={Position.Left}
          className={
            data.waitsForIncoming
              ? "!h-5 !w-[5px] !rounded-none !border-0 !bg-foreground"
              : "!h-[10px] !w-[10px] !border-[1.5px] !border-[var(--agw-agentflow-edge)] !bg-card hover:!scale-150"
          }
        />
      ) : null}
      <Handle
        type="source"
        position={Position.Right}
        className="!h-[10px] !w-[10px] !border-[1.5px] !border-[var(--agw-agentflow-edge)] !bg-card hover:!scale-150"
      />
      <NodeToolbar
        isVisible={selected && canDelete && data.selectedNodeCount === 1}
        position={Position.Top}
        align="end"
        className="flex gap-1"
      >
        <Button
          type="button"
          size="icon-sm"
          variant="secondary"
          onClick={(event) => {
            event.stopPropagation();
            data.onDuplicate(id);
          }}
          aria-label="Duplicate node"
          title="Duplicate"
        >
          <Plus className="h-3.5 w-3.5" />
        </Button>
        <Button
          type="button"
          size="icon-sm"
          variant="secondary"
          onClick={(event) => {
            event.stopPropagation();
            data.onDelete(id);
          }}
          aria-label="Delete node"
          title="Delete"
        >
          <Trash2 className="h-3.5 w-3.5" />
        </Button>
      </NodeToolbar>
      <div className="flex h-[62px] items-center gap-2 px-3">
        <span className="flex h-[30px] w-[30px] shrink-0 items-center justify-center rounded-md bg-muted">
          <Icon className="h-4 w-4 text-muted-foreground" />
        </span>
        <span className="min-w-0 flex-1">
          <span className="block truncate text-sm font-semibold">{node.name ?? meta.label}</span>
          <span className="block truncate text-xs text-muted-foreground">
            {describeNode(node, data.lookups)}
          </span>
        </span>
        <NodeStatusIcon status={runStatus} hasProblem={data.hasProblem} />
      </div>
      {hasMembers ? (
        <div className="border-t border-border px-2 pb-1">
          {node.members.map((member) => {
            const MemberIcon =
              member.kind === "agent" ? Bot : AGENTFLOW_NODE_META.workflowAsAgent.icon;
            return (
              <button
                key={member.memberId}
                type="button"
                className={`flex h-[30px] w-full items-center gap-2 rounded px-1.5 text-left text-xs hover:bg-muted ${data.activeMemberId === member.memberId ? "text-[var(--agw-status-running)]" : ""}`}
                onClick={(event) => {
                  event.stopPropagation();
                  data.onSelectMember(id, member.memberId);
                }}
              >
                <MemberIcon className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
                <span className="min-w-0 flex-1 truncate">{member.name}</span>
                {member.isManager ? (
                  <Crown aria-label="Manager" className="h-3.5 w-3.5 text-muted-foreground" />
                ) : null}
                {data.activeMemberId === member.memberId ? (
                  <LoaderCircle className="h-3.5 w-3.5 animate-spin" />
                ) : null}
              </button>
            );
          })}
          <AgentflowNodeMenu
            agents={data.agents}
            agentflows={data.agentflows}
            currentAgentflowId={data.currentAgentflowId}
            referencesOnly
            onSelect={(kind, reference) => {
              if (reference) data.onAddMember(id, kind, reference);
            }}
            trigger={
              <button
                type="button"
                className="flex h-[30px] w-full items-center gap-2 rounded px-1.5 text-left text-xs text-muted-foreground hover:bg-muted"
                onClick={(event) => event.stopPropagation()}
              >
                <Plus className="h-3.5 w-3.5" /> Add member
              </button>
            }
          />
        </div>
      ) : null}
    </div>
  );
}

function NodeStatusIcon({
  status,
  hasProblem,
}: {
  status: AgentflowNodeStatus | null;
  hasProblem: boolean;
}) {
  if (status === "running")
    return (
      <LoaderCircle
        className="h-4 w-4 animate-spin text-[var(--agw-status-running)]"
        aria-label="Running"
      />
    );
  if (status === "waiting") return <Hand className="h-4 w-4 text-amber-500" aria-label="Waiting" />;
  if (status === "failed")
    return <CircleX className="h-4 w-4 text-destructive" aria-label="Failed" />;
  if (hasProblem)
    return <CircleAlert className="h-4 w-4 text-destructive" aria-label="Has problem" />;
  if (status === "completed")
    return <Check className="h-4 w-4 text-muted-foreground" aria-label="Completed" />;
  return null;
}

function getStatusStyle(status: AgentflowNodeStatus | null): React.CSSProperties | undefined {
  if (status === "running") {
    return {
      borderColor: "var(--agw-status-running)",
      boxShadow: "0 0 0 3px color-mix(in srgb, var(--agw-status-running) 14%, transparent)",
    };
  }
  if (status === "waiting") {
    return {
      borderColor: "var(--color-amber-500)",
      boxShadow: "0 0 0 3px color-mix(in srgb, var(--color-amber-500) 14%, transparent)",
    };
  }
  if (status === "failed") return { borderColor: "var(--destructive)" };
  return undefined;
}
