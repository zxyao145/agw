import { useEffect, useMemo, useRef } from "react";
import { CheckCircle2, Circle, CircleX, Hand, LoaderCircle, Workflow } from "lucide-react";
import { apiGet, getApiErrorMessage, type components } from "@agw/api";
import {
  AGENTFLOW_NODE_LABELS,
  getAgentflowNodeStatus,
  orderAgentflowNodes,
  type AgentflowNodeRunState,
} from "@agw/chat-core";
import { Badge, cn, Skeleton } from "@agw/components";
import { useQuery } from "@agw/components/query";
import { useAgentflowsQuery } from "../../use-agentflows-query";

type AgentflowNode = components["schemas"]["AgentflowNodeDto"];
type AgentflowDefinition = components["schemas"]["AgentflowDefinitionDto"];
type Agent = { id: string; name: string; displayName: string };
type Agentflow = Pick<components["schemas"]["AgentflowDto"], "id" | "name">;
type NodeStatus = ReturnType<typeof getAgentflowNodeStatus>;

const statusDisplay = {
  notStarted: { Icon: Circle, label: "Not started", color: "text-muted-foreground" },
  running: {
    Icon: LoaderCircle,
    label: "Running",
    color: "animate-spin text-(--agw-status-running)",
  },
  waiting: { Icon: Hand, label: "Waiting", color: "text-amber-500" },
  completed: { Icon: CheckCircle2, label: "Completed", color: "text-emerald-600" },
  failed: { Icon: CircleX, label: "Failed", color: "text-destructive" },
};

function getNodeTitle(
  node: AgentflowNode,
  agents: readonly Agent[],
  agentflows: readonly Agentflow[],
) {
  if (node.name?.trim()) return node.name;

  let name: string | null | undefined;
  switch (node.kind) {
    case "agent": {
      const agent = agents.find(({ id }) => id === node.relateId);
      name = agent?.displayName?.trim() ? agent.displayName : agent?.name;
      break;
    }
    case "workflowAsAgent":
      name = agentflows.find(({ id }) => id === node.relateId)?.name;
      break;
    case "checkpointMarker":
      name = node.checkpointName;
      break;
  }
  return name?.trim() ? name : AGENTFLOW_NODE_LABELS[node.kind];
}

function useAgentflowNodeQueries(agentflowId: string, revision: number) {
  const detail = useQuery({
    queryKey: ["agentflow-detail", agentflowId, revision],
    queryFn: () =>
      apiGet("/api/agentflows/detail", { params: { query: { id: agentflowId, revision } } }),
    staleTime: Infinity,
  });
  const agentsQuery = useQuery({
    queryKey: ["agents"],
    queryFn: () => apiGet("/api/agents"),
  });
  const agentflowsQuery = useAgentflowsQuery();
  return { detail, agents: agentsQuery.data ?? [], agentflows: agentflowsQuery.data ?? [] };
}

type ChatAgentflowNodesProps = {
  agentflowId: string;
  revision: number;
  run: AgentflowNodeRunState;
};

export function ChatAgentflowNodes({ agentflowId, revision, run }: ChatAgentflowNodesProps) {
  const { detail, agents, agentflows } = useAgentflowNodeQueries(agentflowId, revision);
  return (
    <section
      className="mt-4 rounded-2xl border border-border bg-background/50 px-3 py-3 shadow-xs"
      aria-label="Agentflow nodes"
    >
      <div className="mb-2 flex items-center gap-2">
        <Workflow className="size-4 shrink-0 text-primary" aria-hidden="true" />
        <h2 className="truncate text-base font-medium text-muted-foreground">
          {agentflows.find(({ id }) => id === agentflowId)?.name ?? "Agentflow"}
        </h2>
        <Badge variant="secondary" className="ml-auto shrink-0">
          v{revision}
        </Badge>
      </div>
      {detail.isPending && (
        <div className="space-y-2">
          <Skeleton className="h-5" />
          <Skeleton className="h-5" />
          <Skeleton className="h-5" />
        </div>
      )}
      {detail.isError && (
        <p className="text-sm text-destructive">{getApiErrorMessage(detail.error)}</p>
      )}
      {detail.isSuccess && (
        <ChatAgentflowNodeList
          agentflowId={agentflowId}
          revision={revision}
          definition={detail.data?.definition}
          run={run}
          agents={agents}
          agentflows={agentflows}
        />
      )}
    </section>
  );
}

type ChatAgentflowNodeListProps = ChatAgentflowNodesProps & {
  definition?: AgentflowDefinition | null;
  agents: readonly Agent[];
  agentflows: readonly Agentflow[];
};

function ChatAgentflowNodeList({
  agentflowId,
  revision,
  definition,
  run,
  agents,
  agentflows,
}: ChatAgentflowNodeListProps) {
  const nodes = useMemo(() => (definition ? orderAgentflowNodes(definition) : []), [definition]);
  const rows = nodes.map((node) => ({ node, status: getAgentflowNodeStatus(run, node.nodeId) }));
  const highlightKey = JSON.stringify(
    rows
      .filter(({ status }) => status === "running" || status === "waiting")
      .map(({ node }) => node.nodeId),
  );
  const list = useRef<HTMLOListElement>(null);
  useEffect(() => {
    list.current?.querySelector('[aria-current="step"]')?.scrollIntoView({ block: "nearest" });
  }, [agentflowId, revision, highlightKey]);

  return (
    <div className="max-h-[min(50vh,28rem)] overflow-y-auto">
      <ol ref={list}>
        {rows.map(({ node, status }) => (
          <ChatAgentflowNodeRow
            key={node.nodeId}
            node={node}
            status={status}
            title={getNodeTitle(node, agents, agentflows)}
            activeMemberId={
              status === "running" ? run.activeMemberIdByNodeId[node.nodeId] : undefined
            }
          />
        ))}
      </ol>
    </div>
  );
}

type ChatAgentflowNodeRowProps = {
  node: AgentflowNode;
  status: NodeStatus;
  title: string;
  activeMemberId?: string;
};

function ChatAgentflowNodeRow({ node, status, title, activeMemberId }: ChatAgentflowNodeRowProps) {
  const label = AGENTFLOW_NODE_LABELS[node.kind];
  const { Icon, label: statusLabel, color } = statusDisplay[status ?? "notStarted"];
  const highlighted = status === "running" || status === "waiting";
  return (
    <li className="mb-0.5">
      <div
        aria-current={highlighted ? "step" : undefined}
        className={cn(
          "flex items-start gap-2 rounded-lg px-2 py-1.5",
          status === "running" &&
            "bg-(--agw-status-running)/10 ring-1 ring-(--agw-status-running)/40 ring-inset",
          status === "waiting" && "bg-amber-500/10 ring-1 ring-amber-500/40 ring-inset",
        )}
      >
        <Icon className={cn("mt-0.5 size-4 shrink-0", color)} role="img" aria-label={statusLabel} />
        <div className="min-w-0">
          <div
            title={title}
            className={cn(
              "truncate text-sm",
              highlighted && "font-medium",
              status === "failed" && "text-destructive",
            )}
          >
            {title}
          </div>
          {title !== label && <div className="truncate text-xs text-muted-foreground">{label}</div>}
        </div>
      </div>
      <ChatAgentflowNodeMembers node={node} activeMemberId={activeMemberId} />
    </li>
  );
}

function ChatAgentflowNodeMembers({
  node,
  activeMemberId,
}: Pick<ChatAgentflowNodeRowProps, "node" | "activeMemberId">) {
  if (!("members" in node) || node.members.length === 0) return null;
  return (
    <ul className="ml-8 mt-1 space-y-0.5">
      {node.members.map((member) => (
        <li
          key={member.memberId}
          className={cn(
            "truncate text-xs",
            member.memberId === activeMemberId
              ? "font-medium text-(--agw-status-running)"
              : "text-muted-foreground",
          )}
          aria-current={member.memberId === activeMemberId ? "true" : undefined}
        >
          {member.name}
        </li>
      ))}
    </ul>
  );
}
