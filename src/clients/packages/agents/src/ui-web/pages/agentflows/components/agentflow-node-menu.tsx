"use client";

import * as React from "react";
import { Input, Popover, PopoverAnchor, PopoverContent, PopoverTrigger } from "@agw/components";

import type { AgentDto, AgentflowDto, AgentflowNodeKind } from "../../../../types/agentflow";
import type { NodeReference } from "./agentflow-document";
import { AGENTFLOW_NODE_META } from "./agentflow-node-meta";
import { matchesNodeLibrarySearch } from "./agentflow-node-library-search";

type AgentflowNodeMenuProps = {
  agents: AgentDto[];
  agentflows: AgentflowDto[];
  currentAgentflowId: string | null;
  referencesOnly?: boolean;
  onSelect(kind: AgentflowNodeKind, reference?: NodeReference): void;
  trigger?: React.ReactNode;
  open?: boolean;
  onOpenChange?(open: boolean): void;
  anchorStyle?: React.CSSProperties;
};

export function AgentflowNodeMenu({
  agents,
  agentflows,
  currentAgentflowId,
  referencesOnly = false,
  onSelect,
  trigger,
  open,
  onOpenChange,
  anchorStyle,
}: AgentflowNodeMenuProps) {
  const [query, setQuery] = React.useState("");
  const [internalOpen, setInternalOpen] = React.useState(false);
  const isOpen = open ?? internalOpen;
  const setOpen = onOpenChange ?? setInternalOpen;
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
  const groupedKinds = (group: "steps" | "blocks") =>
    Object.values(AGENTFLOW_NODE_META).filter(
      (item) =>
        item.group === group && matchesNodeLibrarySearch(query, [item.label, item.description]),
    );

  const select = (kind: AgentflowNodeKind, reference?: NodeReference) => {
    onSelect(kind, reference);
    setQuery("");
    setOpen(false);
  };

  return (
    <Popover open={isOpen} onOpenChange={setOpen}>
      {trigger ? <PopoverTrigger asChild>{trigger}</PopoverTrigger> : null}
      {anchorStyle ? (
        <PopoverAnchor
          aria-hidden="true"
          className="pointer-events-none absolute h-px w-px"
          style={anchorStyle}
        />
      ) : null}
      <PopoverContent side={anchorStyle ? "right" : "bottom"} align="start" className="w-80 p-2">
        <Input
          autoFocus
          value={query}
          onChange={(event) => setQuery(event.target.value)}
          placeholder="Search nodes"
          aria-label="Search nodes"
        />
        <div className="mt-2 max-h-80 overflow-y-auto">
          {!referencesOnly ? (
            <>
              <NodeGroup title="Steps">
                {groupedKinds("steps").map((item) => (
                  <NodeOption
                    key={item.kind}
                    label={item.label}
                    description={item.description}
                    icon={item.icon}
                    onClick={() => select(item.kind)}
                  />
                ))}
              </NodeGroup>
              <NodeGroup title="Blocks">
                {groupedKinds("blocks").map((item) => (
                  <NodeOption
                    key={item.kind}
                    label={item.label}
                    description={item.description}
                    icon={item.icon}
                    onClick={() => select(item.kind)}
                  />
                ))}
              </NodeGroup>
            </>
          ) : null}
          <NodeGroup title="Agents">
            {visibleAgents.map((agent) => (
              <NodeOption
                key={agent.id}
                label={agent.name}
                description={agent.description}
                icon={AGENTFLOW_NODE_META.agent.icon}
                onClick={() =>
                  select("agent", { kind: "agent", relateId: agent.id, name: agent.name })
                }
              />
            ))}
          </NodeGroup>
          <NodeGroup title="Workflows">
            {visibleAgentflows.map((agentflow) => (
              <NodeOption
                key={agentflow.id}
                label={agentflow.name}
                description={agentflow.description || "Nested Agentflow"}
                icon={AGENTFLOW_NODE_META.workflowAsAgent.icon}
                onClick={() =>
                  select("workflowAsAgent", {
                    kind: "workflowAsAgent",
                    relateId: agentflow.id,
                    name: agentflow.name,
                  })
                }
              />
            ))}
          </NodeGroup>
          {!referencesOnly &&
          !visibleAgents.length &&
          !visibleAgentflows.length &&
          !groupedKinds("steps").length &&
          !groupedKinds("blocks").length ? (
            <p className="px-2 py-3 text-sm text-muted-foreground">No nodes found.</p>
          ) : null}
        </div>
      </PopoverContent>
    </Popover>
  );
}

function NodeGroup({ title, children }: React.PropsWithChildren<{ title: string }>) {
  if (!children) return null;
  return (
    <section className="py-1">
      <h3 className="px-2 py-1 text-xs font-medium text-muted-foreground">{title}</h3>
      <div className="space-y-0.5">{children}</div>
    </section>
  );
}

function NodeOption({
  label,
  description,
  icon: Icon,
  onClick,
}: {
  label: string;
  description: string;
  icon: typeof AGENTFLOW_NODE_META.agent.icon;
  onClick(): void;
}) {
  return (
    <button
      type="button"
      className="flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left hover:bg-accent"
      onClick={onClick}
    >
      <Icon className="h-4 w-4 shrink-0 text-muted-foreground" />
      <span className="min-w-0 flex-1">
        <span className="block truncate text-sm">{label}</span>
        <span className="block truncate text-xs text-muted-foreground">{description}</span>
      </span>
    </button>
  );
}
