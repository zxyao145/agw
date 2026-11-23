"use client";

import * as React from "react";
import { Input } from "@agw/components";

import type { AgentDto, AgentflowDto, AgentflowNodeKind } from "../../../../types/agentflow";
import type { NodeReference } from "./agentflow-document";
import { AGENTFLOW_NODE_META, type AgentflowNodeMeta } from "./agentflow-node-meta";
import { matchesNodeLibrarySearch } from "./agentflow-node-library-search";

export const AGENTFLOW_NODE_DRAG_TYPE = "application/x-agw-agentflow-node";

type AgentflowNodeLibraryProps = {
  agents: AgentDto[];
  agentflows: AgentflowDto[];
  currentAgentflowId: string | null;
  onAddNode(kind: AgentflowNodeKind, reference?: NodeReference): void;
};

export function AgentflowNodeLibrary({
  agents,
  agentflows,
  currentAgentflowId,
  onAddNode,
}: AgentflowNodeLibraryProps) {
  const [query, setQuery] = React.useState("");
  const visibleAgents = agents.filter(
    (agent) =>
      agent.enable &&
      matchesNodeLibrarySearch(query, [
        agent.name,
        agent.description,
        AGENTFLOW_NODE_META.agent.label,
      ]),
  );
  const visibleAgentflows = agentflows.filter(
    (agentflow) =>
      agentflow.id !== currentAgentflowId &&
      matchesNodeLibrarySearch(query, [
        agentflow.name,
        agentflow.description ?? "",
        AGENTFLOW_NODE_META.workflowAsAgent.label,
      ]),
  );
  const visibleKinds = (group: "steps" | "blocks") =>
    Object.values(AGENTFLOW_NODE_META).filter(
      (item) =>
        item.group === group && matchesNodeLibrarySearch(query, [item.label, item.description]),
    );

  const beginDrag = (
    event: React.DragEvent,
    kind: AgentflowNodeKind,
    reference?: NodeReference,
  ) => {
    event.dataTransfer.setData(AGENTFLOW_NODE_DRAG_TYPE, JSON.stringify({ kind, reference }));
    event.dataTransfer.effectAllowed = "copy";
  };

  return (
    <aside className="flex h-full min-h-0 flex-col border-r bg-background">
      <div className="border-b p-3">
        <h2 className="mb-2 text-sm font-semibold">Node library</h2>
        <Input
          value={query}
          onChange={(event) => setQuery(event.target.value)}
          placeholder="Search nodes"
          aria-label="Search nodes"
        />
      </div>
      <div className="min-h-0 flex-1 overflow-y-auto p-2">
        <NodeSection title="Agents">
          {visibleAgents.map((agent) => (
            <LibraryItem
              key={agent.id}
              label={agent.name}
              description={agent.description}
              meta={AGENTFLOW_NODE_META.agent}
              onClick={() =>
                onAddNode("agent", { kind: "agent", relateId: agent.id, name: agent.name })
              }
              onDragStart={(event) =>
                beginDrag(event, "agent", { kind: "agent", relateId: agent.id, name: agent.name })
              }
            />
          ))}
        </NodeSection>
        <NodeSection title="Workflows">
          {visibleAgentflows.map((agentflow) => (
            <LibraryItem
              key={agentflow.id}
              label={agentflow.name}
              description={agentflow.description || "Nested Agentflow"}
              meta={AGENTFLOW_NODE_META.workflowAsAgent}
              onClick={() =>
                onAddNode("workflowAsAgent", {
                  kind: "workflowAsAgent",
                  relateId: agentflow.id,
                  name: agentflow.name,
                })
              }
              onDragStart={(event) =>
                beginDrag(event, "workflowAsAgent", {
                  kind: "workflowAsAgent",
                  relateId: agentflow.id,
                  name: agentflow.name,
                })
              }
            />
          ))}
        </NodeSection>
        <NodeSection title="Steps">
          {visibleKinds("steps").map((meta) => (
            <LibraryItem
              key={meta.kind}
              label={meta.label}
              description={meta.description}
              meta={meta}
              onClick={() => onAddNode(meta.kind)}
              onDragStart={(event) => beginDrag(event, meta.kind)}
            />
          ))}
        </NodeSection>
        <NodeSection title="Blocks">
          {visibleKinds("blocks").map((meta) => (
            <LibraryItem
              key={meta.kind}
              label={meta.label}
              description={meta.description}
              meta={meta}
              onClick={() => onAddNode(meta.kind)}
              onDragStart={(event) => beginDrag(event, meta.kind)}
            />
          ))}
        </NodeSection>
      </div>
    </aside>
  );
}

function NodeSection({ title, children }: React.PropsWithChildren<{ title: string }>) {
  return (
    <section className="mb-3">
      <h3 className="px-2 py-1 text-xs font-semibold uppercase tracking-wide text-muted-foreground">
        {title}
      </h3>
      <div className="space-y-1">{children}</div>
    </section>
  );
}

function LibraryItem({
  label,
  description,
  meta,
  onClick,
  onDragStart,
}: {
  label: string;
  description: string;
  meta: AgentflowNodeMeta;
  onClick(): void;
  onDragStart(event: React.DragEvent): void;
}) {
  const Icon = meta.icon;
  return (
    <button
      type="button"
      draggable
      onDragStart={onDragStart}
      onClick={onClick}
      className="flex w-full items-center gap-2 rounded-md border border-transparent px-2 py-2 text-left hover:border-border hover:bg-muted/50"
    >
      <span className="flex h-8 w-8 shrink-0 items-center justify-center rounded-md bg-muted">
        <Icon className="h-4 w-4 text-muted-foreground" />
      </span>
      <span className="min-w-0 flex-1">
        <span className="block truncate text-sm font-medium">{label}</span>
        <span className="block truncate text-xs text-muted-foreground">{description}</span>
      </span>
    </button>
  );
}
