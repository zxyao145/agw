"use client";

import * as React from "react";
import ReactFlow, {
  Background,
  BackgroundVariant,
  Connection,
  ControlButton,
  Controls,
  Edge,
  EdgeChange,
  Handle,
  MarkerType,
  Node,
  NodeChange,
  NodeProps,
  NodeTypes,
  OnSelectionChangeParams,
  Position,
  ReactFlowInstance,
  addEdge,
  useEdgesState,
  useNodesState,
  useReactFlow,
} from "reactflow";
import "reactflow/dist/style.css";

import { apiPost, apiPut } from "@/api/client";
import {
  SearchableSelect,
  type SearchableSelectOption,
} from "@/components/SearchableSelect/searchable-select";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Switch } from "@/components/ui/switch";
import { Textarea } from "@/components/ui/textarea";
import {
  AgentDto,
  AgentflowDetailDto,
  AgentflowDto,
  AgentflowEdgeDto,
  AgentflowEdgeKind,
  AgentflowNodeDto,
  AgentflowNodeKind,
  AgentflowSaveRequest,
  ModelProviderDto,
} from "@/types/agentflow";
import {
  ArrowLeft,
  ChevronRight,
  Crown,
  ExternalLink,
  Grid,
  Maximize2,
  Trash2,
  Users,
  X,
} from "lucide-react";
import { toast } from "sonner";
import { createGraphLayout } from "./autoLayout";
import {
  addBlockParticipantId,
  canDeleteBlockMember,
  createBlockMembership,
  getBlockParticipantEdges,
  getBlockParticipantNodes,
  getNextBlockParticipantPosition,
  getVisibleEdges,
  getVisibleNodes,
  isAgentParticipantKind,
  isBlockNodeKind,
  removeBlockParticipantId,
  type BlockMemberView,
  type BlockMembership,
} from "./block-membership";
import {
  createInputNode,
  ensureInputGraph,
  INPUT_NODE_ID,
  validateInputGraph,
} from "./agentflow-input-node";

type DagNodeData = {
  kind: AgentflowNodeKind;
  title: string;
  relateId: string | null;
  instructions: string;
  configJson: string;
  presentation?: {
    disableHandles?: boolean;
    member?: BlockMemberView;
    members?: BlockMemberView[];
    onDelete?: (nodeId: string) => void;
    onOpenBlock?: (blockId: string) => void;
  };
};

type DagEdgeData = {
  kind: AgentflowEdgeKind;
  label: string;
  conditionJson: string;
  configJson: string;
};

type VisualAgentflowBuilderProps = {
  agents: AgentDto[];
  agentflows?: AgentflowDto[];
  modelProviders: ModelProviderDto[];
  editingAgentflow?: AgentflowDetailDto | null;
  onAgentflowCreated?: () => void;
  onActionStateChange?: (state: AgentflowBuilderActionState | null) => void;
};

export type AgentflowBuilderActionState = {
  label: string;
  disabled: boolean;
  isSaving: boolean;
  submit: () => void;
};

type CanvasScope =
  | {
      kind: "root";
    }
  | {
      kind: "block";
      blockId: string;
    };

const NODE_META: Record<
  AgentflowNodeKind,
  { label: string; symbol: string; tone: string; body: string }
> = {
  [AgentflowNodeKind.Agent]: {
    label: "Agent",
    symbol: "A",
    tone: "border-teal-200 bg-teal-50 text-teal-800",
    body: "Runtime AI agent",
  },
  [AgentflowNodeKind.WorkflowAsAgent]: {
    label: "Workflow as Agent",
    symbol: "W",
    tone: "border-sky-200 bg-sky-50 text-sky-800",
    body: "Nested Agentflow exposed as an AI agent",
  },
  [AgentflowNodeKind.PromptAdapter]: {
    label: "Prompt Adapter",
    symbol: "P",
    tone: "border-zinc-200 bg-zinc-50 text-zinc-800",
    body: "Transform upstream output before the next node",
  },
  [AgentflowNodeKind.HumanGate]: {
    label: "Human Gate",
    symbol: "H",
    tone: "border-rose-200 bg-rose-50 text-rose-800",
    body: "Pause for human approval or input",
  },
  [AgentflowNodeKind.CheckpointMarker]: {
    label: "Checkpoint",
    symbol: "C",
    tone: "border-emerald-200 bg-emerald-50 text-emerald-800",
    body: "Expose a named resumable point",
  },
  [AgentflowNodeKind.ConcurrentBlock]: {
    label: "Concurrent Block",
    symbol: "||",
    tone: "border-amber-200 bg-amber-50 text-amber-900",
    body: "Fan out and join branches",
  },
  [AgentflowNodeKind.HandoffBlock]: {
    label: "Handoff Block",
    symbol: "R",
    tone: "border-amber-200 bg-amber-50 text-amber-900",
    body: "Dynamic runtime routing between agents",
  },
  [AgentflowNodeKind.GroupChatBlock]: {
    label: "GroupChat Room",
    symbol: "G",
    tone: "border-violet-200 bg-violet-50 text-violet-900",
    body: "Managed multi-agent conversation",
  },
  [AgentflowNodeKind.MagenticBlock]: {
    label: "Magentic Team",
    symbol: "M",
    tone: "border-blue-200 bg-blue-50 text-blue-900",
    body: "Planner and workers with stall/reset policy",
  },
  [AgentflowNodeKind.Output]: {
    label: "Output",
    symbol: "O",
    tone: "border-zinc-200 bg-zinc-50 text-zinc-900",
    body: "Terminal workflow output",
  },
  [AgentflowNodeKind.Input]: {
    label: "Input",
    symbol: "I",
    tone: "border-indigo-200 bg-indigo-50 text-indigo-900",
    body: "User input that starts the agentflow",
  },
};

const EDGE_LABELS: Record<AgentflowEdgeKind, string> = {
  [AgentflowEdgeKind.Direct]: "Direct",
  [AgentflowEdgeKind.FanOut]: "Fan Out",
  [AgentflowEdgeKind.FanIn]: "Fan In",
};

const EDGE_HELP_TEXT: Record<AgentflowEdgeKind, string> = {
  [AgentflowEdgeKind.Direct]:
    "MAF AddEdge: one source to one target, optionally guarded by a predicate.",
  [AgentflowEdgeKind.FanOut]:
    "MAF AddFanOutEdge: one source broadcasts the same input to multiple targets.",
  [AgentflowEdgeKind.FanIn]:
    "MAF AddFanInBarrierEdge: multiple sources join before the target runs.",
};

const CONDITION_KEYS = new Set([
  "always",
  "contains",
  "notContains",
  "equals",
  "author",
  "role",
  "minMessages",
]);

// connection point style
const HANDLE_STYLE: React.CSSProperties = {
  width: 18,
  height: 18,
  zIndex: 10,
  borderWidth: 3,
  borderColor: "var(--background)",
};

const HANDLE_IN_STYLE: React.CSSProperties = {
  ...HANDLE_STYLE,
  left: "-7px",
};

const HANDLE_OUT_STYLE: React.CSSProperties = {
  ...HANDLE_STYLE,
  right: "-7px",
};

function DagNode({ id, data, selected }: NodeProps<DagNodeData>) {
  const meta = NODE_META[data.kind];
  const member = data.presentation?.member;
  const members = data.presentation?.members ?? [];
  const isInput = data.kind === AgentflowNodeKind.Input;
  const isBlock = isBlockNodeKind(data.kind);
  const showHandles = !data.presentation?.disableHandles;
  const showOpenBlock = isBlock && data.presentation?.onOpenBlock;
  const hasBlockWarning = members.some(
    (member) => member.isExternallyLinked || member.isShared || member.isMissing,
  );
  const headerPadding = isInput ? "" : showOpenBlock ? "pr-[4.75rem]" : "pr-9";

  return (
    <div className="relative w-[220px]">
      {!isInput && showHandles ? (
        <Handle
          type="target"
          position={Position.Left}
          className="!bg-sky-600"
          style={HANDLE_IN_STYLE}
        />
      ) : null}
      <Card
        className={`relative w-full gap-0 overflow-hidden rounded-md border-2 p-0 shadow-sm transition-shadow ${
          selected
            ? "border-primary shadow-md"
            : hasBlockWarning
              ? "border-amber-400 shadow-amber-100"
              : "border-border"
        }`}
      >
        {isInput ? null : (
          <button
            type="button"
            title="Delete node"
            className="nodrag nopan absolute right-1.5 top-1.5 z-10 grid h-7 w-7 place-items-center rounded border border-border/70 bg-background/90 text-muted-foreground shadow-sm transition-colors hover:border-destructive/40 hover:bg-destructive/10 hover:text-destructive"
            onPointerDown={(event) => event.stopPropagation()}
            onClick={(event) => {
              event.stopPropagation();
              data.presentation?.onDelete?.(id);
            }}
          >
            <Trash2 className="h-3.5 w-3.5" />
          </button>
        )}
        {showOpenBlock ? (
          <button
            type="button"
            title="Open block"
            className="nodrag nopan absolute right-10 top-1.5 z-10 grid h-7 w-7 place-items-center rounded border border-border/70 bg-background/90 text-muted-foreground shadow-sm transition-colors hover:border-primary/40 hover:bg-primary/10 hover:text-primary"
            onPointerDown={(event) => event.stopPropagation()}
            onClick={(event) => {
              event.stopPropagation();
              data.presentation?.onOpenBlock?.(id);
            }}
          >
            <ExternalLink className="h-3.5 w-3.5" />
          </button>
        ) : null}
        <CardHeader className={`px-3 py-2 ${headerPadding} ${meta.tone}`}>
          <div className="flex min-w-0 items-center gap-2">
            <div className="grid h-7 w-7 shrink-0 place-items-center rounded border bg-background/80 text-xs font-semibold">
              {meta.symbol}
            </div>
            <div className="min-w-0">
              <CardTitle className="truncate text-sm">{data.title}</CardTitle>
              <div className="mt-0.5 text-[10px] uppercase tracking-wide opacity-70">
                {meta.label}
              </div>
            </div>
          </div>
        </CardHeader>
        <CardContent className="px-3 py-2 text-xs text-muted-foreground">
          {isBlock ? (
            <BlockNodeSummary kind={data.kind} members={members} fallback={meta.body} />
          ) : member ? (
            <BlockParticipantSummary member={member} fallback={meta.body} />
          ) : (
            meta.body
          )}
        </CardContent>
      </Card>
      {showHandles ? (
        <Handle
          type="source"
          position={Position.Right}
          className="!bg-emerald-600"
          style={HANDLE_OUT_STYLE}
        />
      ) : null}
    </div>
  );
}

function BlockParticipantSummary({
  member,
  fallback,
}: {
  member: BlockMemberView;
  fallback: string;
}) {
  return (
    <div className="space-y-2">
      <span>{fallback}</span>
      <div className="flex flex-wrap gap-1">
        {member.isManager ? (
          <span className="inline-flex items-center gap-1 rounded-full border border-blue-200 bg-blue-50 px-2 py-0.5 text-[10px] text-blue-700">
            <Crown className="h-3 w-3" />
            Manager
          </span>
        ) : null}
        {member.isExternallyLinked ? (
          <span className="rounded-full border border-amber-200 bg-amber-50 px-2 py-0.5 text-[10px] text-amber-800">
            On Canvas
          </span>
        ) : null}
        {member.isShared ? (
          <span className="rounded-full border border-amber-200 bg-amber-50 px-2 py-0.5 text-[10px] text-amber-800">
            Shared
          </span>
        ) : null}
      </div>
    </div>
  );
}

function BlockNodeSummary({
  kind,
  members,
  fallback,
}: {
  kind: AgentflowNodeKind;
  members: BlockMemberView[];
  fallback: string;
}) {
  const visibleMembers = members.slice(0, 3);
  const hiddenCount = Math.max(0, members.length - visibleMembers.length);
  const manager =
    kind === AgentflowNodeKind.MagenticBlock ? members.find((member) => member.isManager) : null;
  const warningCount = members.filter(
    (member) => member.isExternallyLinked || member.isShared || member.isMissing,
  ).length;

  if (members.length === 0) {
    return <span>{fallback}</span>;
  }

  return (
    <div className="space-y-2">
      <div className="flex items-center justify-between gap-2">
        <span className="inline-flex items-center gap-1 font-medium text-foreground">
          <Users className="h-3.5 w-3.5" />
          {members.length} {members.length === 1 ? "member" : "members"}
        </span>
        {warningCount > 0 ? (
          <span className="rounded-full bg-amber-100 px-2 py-0.5 text-[10px] text-amber-800">
            {warningCount} on canvas
          </span>
        ) : null}
      </div>
      <div className="flex flex-wrap gap-1">
        {visibleMembers.map((member) => (
          <span
            key={member.nodeId}
            className={`max-w-[92px] truncate rounded-full border px-2 py-0.5 text-[10px] ${
              member.isExternallyLinked || member.isShared || member.isMissing
                ? "border-amber-200 bg-amber-50 text-amber-800"
                : "border-teal-200 bg-teal-50 text-teal-800"
            }`}
            title={member.title}
          >
            {member.title}
          </span>
        ))}
        {hiddenCount > 0 ? (
          <span className="rounded-full border bg-muted px-2 py-0.5 text-[10px] text-muted-foreground">
            +{hiddenCount}
          </span>
        ) : null}
      </div>
      {manager ? (
        <div className="flex min-w-0 items-center gap-1 text-[10px] text-blue-700">
          <span>Manager</span>
          <span className="truncate rounded-full border border-blue-200 bg-blue-50 px-2 py-0.5">
            {manager.title}
          </span>
        </div>
      ) : null}
    </div>
  );
}

const nodeTypes: NodeTypes = {
  dagNode: DagNode,
};

function FlowControls({ onAutoLayout }: { onAutoLayout: () => void }) {
  const { fitView } = useReactFlow();

  return (
    <Controls>
      <ControlButton onClick={() => fitView({ padding: 0.2, duration: 200 })} title="Fit view">
        <Maximize2 size={16} />
      </ControlButton>
      <ControlButton onClick={onAutoLayout} title="Auto layout">
        <Grid size={16} />
      </ControlButton>
    </Controls>
  );
}

export function VisualAgentflowBuilder({
  agents,
  agentflows = [],
  modelProviders,
  editingAgentflow,
  onAgentflowCreated,
  onActionStateChange,
}: VisualAgentflowBuilderProps) {
  const [nodes, setNodes, onNodesChange] = useNodesState<DagNodeData>([]);
  const [edges, setEdges, onEdgesChange] = useEdgesState<DagEdgeData>([]);
  const [selectedNodeId, setSelectedNodeId] = React.useState<string | null>(null);
  const [selectedEdgeId, setSelectedEdgeId] = React.useState<string | null>(null);
  const [canvasScope, setCanvasScope] = React.useState<CanvasScope>({ kind: "root" });
  const [reactFlowCanvas, setReactFlowCanvas] = React.useState<{
    key: string;
    instance: ReactFlowInstance<DagNodeData, DagEdgeData>;
  } | null>(null);
  const [pendingFocusNodeId, setPendingFocusNodeId] = React.useState<string | null>(null);
  const [agentflowName, setAgentflowName] = React.useState("");
  const [agentflowDescription, setAgentflowDescription] = React.useState("");
  const [summaryModelProviderId, setSummaryModelProviderId] = React.useState("");
  const [isSaving, setIsSaving] = React.useState(false);

  const availableAgentflows = React.useMemo(() => {
    if (!editingAgentflow) return agentflows;
    return agentflows.filter((agentflow) => agentflow.id !== editingAgentflow.id);
  }, [agentflows, editingAgentflow]);
  const agentSelectOptions = React.useMemo<SearchableSelectOption[]>(
    () =>
      agents.map((agent) => ({
        value: agent.id,
        title: agent.name,
        subtitle: agent.description?.trim() || undefined,
      })),
    [agents],
  );
  const agentflowSelectOptions = React.useMemo<SearchableSelectOption[]>(
    () =>
      availableAgentflows.map((agentflow) => ({
        value: agentflow.id,
        title: agentflow.name,
        subtitle: agentflow.description?.trim() || undefined,
      })),
    [availableAgentflows],
  );

  const deleteFlowNode = React.useCallback(
    (nodeId: string) => {
      if (nodeId === INPUT_NODE_ID) {
        return;
      }

      setNodes((current) =>
        current
          .filter((node) => node.id !== nodeId)
          .map((node) =>
            isBlockNodeKind(node.data.kind)
              ? {
                  ...node,
                  data: {
                    ...node.data,
                    configJson: removeBlockParticipantId(node.data.configJson, nodeId),
                  },
                }
              : node,
          ),
      );
      setEdges((current) =>
        current.filter((edge) => edge.source !== nodeId && edge.target !== nodeId),
      );
      setSelectedNodeId((current) => (current === nodeId ? null : current));
      setSelectedEdgeId(null);
    },
    [setEdges, setNodes],
  );

  const handleNodesChange = React.useCallback(
    (changes: NodeChange[]) => {
      onNodesChange(changes.filter((change) => change.type !== "remove"));
    },
    [onNodesChange],
  );

  const handleEdgesChange = React.useCallback(
    (changes: EdgeChange[]) => {
      if (canvasScope.kind === "block") return;
      onEdgesChange(changes);
    },
    [canvasScope, onEdgesChange],
  );

  const blockMembership = React.useMemo(() => createBlockMembership(nodes, edges), [edges, nodes]);
  const activeBlockNode = React.useMemo(() => {
    if (canvasScope.kind !== "block") return null;
    const node = nodes.find((item) => item.id === canvasScope.blockId) ?? null;
    return node && isBlockNodeKind(node.data.kind) ? node : null;
  }, [canvasScope, nodes]);
  const openBlockScope = React.useCallback((blockId: string) => {
    setPendingFocusNodeId(null);
    setCanvasScope({ kind: "block", blockId });
    setSelectedNodeId(blockId);
    setSelectedEdgeId(null);
  }, []);
  const exitBlockScope = React.useCallback(() => {
    setPendingFocusNodeId(null);
    if (canvasScope.kind === "block") {
      setSelectedNodeId(canvasScope.blockId);
      setSelectedEdgeId(null);
    }
    setCanvasScope({ kind: "root" });
  }, [canvasScope]);
  const selectBlockParticipant = React.useCallback((blockId: string, participantNodeId: string) => {
    setCanvasScope({ kind: "block", blockId });
    setSelectedNodeId(participantNodeId);
    setSelectedEdgeId(null);
  }, []);
  const rootVisibleNodes = React.useMemo(() => {
    return getVisibleNodes(nodes, blockMembership).map((node) => {
      return {
        ...node,
        selected: node.id === selectedNodeId,
        data: {
          ...node.data,
          presentation: {
            ...node.data.presentation,
            members: isBlockNodeKind(node.data.kind)
              ? (blockMembership.membersByBlockId.get(node.id) ?? [])
              : node.data.presentation?.members,
            onDelete: deleteFlowNode,
            onOpenBlock: openBlockScope,
          },
        },
      };
    });
  }, [blockMembership, deleteFlowNode, nodes, openBlockScope, selectedNodeId]);
  const rootVisibleEdges = React.useMemo(
    () => getVisibleEdges(edges, blockMembership),
    [blockMembership, edges],
  );

  React.useEffect(() => {
    if (canvasScope.kind !== "root") return;
    if (!selectedNodeId || !blockMembership.hiddenParticipantIds.has(selectedNodeId)) return;

    setSelectedNodeId(blockMembership.participantOwnersByNodeId.get(selectedNodeId)?.[0] ?? null);
    setSelectedEdgeId(null);
  }, [blockMembership, canvasScope, selectedNodeId]);

  React.useEffect(() => {
    if (canvasScope.kind === "root") return;
    if (activeBlockNode) return;

    setCanvasScope({ kind: "root" });
    setSelectedNodeId(null);
    setSelectedEdgeId(null);
  }, [activeBlockNode, canvasScope]);

  const updateNodeData = React.useCallback(
    (nodeId: string, update: Partial<DagNodeData>) => {
      setNodes((current) =>
        current.map((node) =>
          node.id === nodeId ? { ...node, data: { ...node.data, ...update } } : node,
        ),
      );
    },
    [setNodes],
  );

  const updateEdgeData = React.useCallback(
    (edgeId: string, update: Partial<DagEdgeData>) => {
      setEdges((current) =>
        current.map((edge) => {
          if (edge.id !== edgeId) return edge;

          const guardedUpdate =
            edge.source === INPUT_NODE_ID
              ? {
                  ...update,
                  kind: AgentflowEdgeKind.FanOut,
                  conditionJson: "",
                }
              : update;
          const nextData = { ...createDefaultEdgeData(), ...edge.data, ...guardedUpdate };
          return applyEdgeVisuals({
            ...edge,
            data: nextData,
            label: nextData.label || undefined,
          });
        }),
      );
    },
    [setEdges],
  );

  const addDagNode = React.useCallback(
    (kind: AgentflowNodeKind, title: string, relateId: string | null = null) => {
      const nodeId = `${kind}-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
      const offset = nodes.length * 28;
      const node: Node<DagNodeData> = {
        id: nodeId,
        type: "dagNode",
        position: { x: 160 + offset, y: 120 + offset },
        data: {
          kind,
          title,
          relateId,
          instructions: "",
          configJson: "",
        },
      };

      setNodes((current) => [...current, node]);
      setSelectedNodeId(nodeId);
      setSelectedEdgeId(null);
    },
    [nodes.length, setNodes],
  );

  React.useEffect(() => {
    if (!editingAgentflow) {
      setAgentflowName("");
      setAgentflowDescription("");
      setSummaryModelProviderId("");
      setNodes([createInputNode<DagNodeData>()]);
      setEdges([]);
      setCanvasScope({ kind: "root" });
      setPendingFocusNodeId(null);
      setSelectedNodeId(INPUT_NODE_ID);
      setSelectedEdgeId(null);
      return;
    }

    setAgentflowName(editingAgentflow.name);
    setAgentflowDescription(editingAgentflow.description || "");
    setSummaryModelProviderId(editingAgentflow.summaryModelProviderId ?? "");

    const loadedNodes = editingAgentflow.nodes.map((node, index) => {
      const position = parsePosition(node.positionJson, index);
      return {
        id: node.nodeId,
        type: "dagNode",
        position,
        data: {
          kind: node.kind,
          title: node.name || resolveNodeTitle(node, agents, agentflows),
          relateId: node.relateId,
          instructions: node.instructions || "",
          configJson: node.configJson || "",
        },
      } satisfies Node<DagNodeData>;
    });

    const loadedEdges = editingAgentflow.edges.map((edge) => createFlowEdge(edge));
    const normalizedGraph = ensureInputGraph(loadedNodes, loadedEdges);
    setNodes(normalizedGraph.nodes);
    setEdges(normalizedGraph.edges.map((edge) => applyEdgeVisuals(edge)));
    setCanvasScope({ kind: "root" });
    setPendingFocusNodeId(null);
    setSelectedNodeId(INPUT_NODE_ID);
    setSelectedEdgeId(null);
  }, [agents, agentflows, editingAgentflow, setEdges, setNodes]);

  const onConnect = React.useCallback(
    (params: Connection) => {
      if (canvasScope.kind === "block") return;
      if (!params.source || !params.target) return;
      if (params.target === INPUT_NODE_ID) {
        toast.error("Input cannot have incoming edges");
        return;
      }

      const edgeData = createDefaultEdgeData(
        params.source === INPUT_NODE_ID ? AgentflowEdgeKind.FanOut : AgentflowEdgeKind.Direct,
      );

      const edge: Edge<DagEdgeData> = {
        id: `edge-${params.source}-${params.target}-${Date.now()}`,
        source: params.source,
        target: params.target,
        sourceHandle: params.sourceHandle,
        targetHandle: params.targetHandle,
        data: edgeData,
      };

      setEdges((current) => addEdge(applyEdgeVisuals(edge), current));
    },
    [canvasScope, setEdges],
  );

  const onSelectionChange = React.useCallback(
    (selection: OnSelectionChangeParams) => {
      const selectedNode = selection.nodes[0];
      const selectedEdge = selection.edges[0];

      if (selectedNode) {
        setSelectedNodeId(selectedNode.id);
        setSelectedEdgeId(null);
        return;
      }

      if (selectedEdge && canvasScope.kind === "root") {
        setSelectedEdgeId(selectedEdge.id);
        setSelectedNodeId(null);
        return;
      }

      // ReactFlow can emit a transient empty selection while controlled node selection is syncing.
      // Pane clicks own the intentional clear/select-parent behavior below.
    },
    [canvasScope],
  );

  const addBlockParticipant = React.useCallback(
    (blockId: string, kind: AgentflowNodeKind, title: string, relateId: string) => {
      const nodeId = `${kind}-participant-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;

      setNodes((current) => {
        const blockNode = current.find((node) => node.id === blockId);
        if (!blockNode) return current;

        const participantNode: Node<DagNodeData> = {
          id: nodeId,
          type: "dagNode",
          position: getNextBlockParticipantPosition(current, blockId),
          data: {
            kind,
            title,
            relateId,
            instructions: "",
            configJson: "",
          },
        };

        return [
          ...current.map((node) =>
            node.id === blockId
              ? {
                  ...node,
                  data: {
                    ...node.data,
                    configJson: addBlockParticipantId(node.data.configJson, nodeId),
                  },
                }
              : node,
          ),
          participantNode,
        ];
      });
      setCanvasScope({ kind: "block", blockId });
      setPendingFocusNodeId(nodeId);
      setSelectedNodeId(nodeId);
      setSelectedEdgeId(null);
    },
    [setNodes],
  );

  const removeBlockParticipant = React.useCallback(
    (blockId: string, participantNodeId: string) => {
      setNodes((current) =>
        current.map((node) =>
          node.id === blockId
            ? {
                ...node,
                data: {
                  ...node.data,
                  configJson: removeBlockParticipantId(node.data.configJson, participantNodeId),
                },
              }
            : node,
        ),
      );
      setSelectedNodeId(blockId);
      setSelectedEdgeId(null);
    },
    [setNodes],
  );

  const deleteBlockParticipant = React.useCallback(
    (blockId: string, participantNodeId: string) => {
      if (!canDeleteBlockMember(blockMembership, participantNodeId)) {
        setNodes((current) =>
          current.map((node) =>
            node.id === blockId
              ? {
                  ...node,
                  data: {
                    ...node.data,
                    configJson: removeBlockParticipantId(node.data.configJson, participantNodeId),
                  },
                }
              : node,
          ),
        );
        setCanvasScope({ kind: "root" });
        setSelectedNodeId(participantNodeId);
        setSelectedEdgeId(null);
        toast.info("Member removed from this block and kept in the workflow.");
        return;
      }

      deleteFlowNode(participantNodeId);
      setSelectedNodeId(blockId);
      setSelectedEdgeId(null);
    },
    [blockMembership, deleteFlowNode, setNodes],
  );

  const blockCanvasNodes = React.useMemo(() => {
    if (!activeBlockNode) return [];

    const members = blockMembership.membersByBlockId.get(activeBlockNode.id) ?? [];
    const memberByNodeId = new Map(members.map((member) => [member.nodeId, member]));

    return getBlockParticipantNodes(nodes, activeBlockNode.id).map((node) => ({
      ...node,
      selected: node.id === selectedNodeId,
      data: {
        ...node.data,
        presentation: {
          ...node.data.presentation,
          disableHandles: true,
          member: memberByNodeId.get(node.id),
          onDelete: (nodeId: string) => deleteBlockParticipant(activeBlockNode.id, nodeId),
        },
      },
    }));
  }, [activeBlockNode, blockMembership, deleteBlockParticipant, nodes, selectedNodeId]);
  const blockCanvasEdges = React.useMemo(() => {
    if (!activeBlockNode) return [];
    return getBlockParticipantEdges(edges, activeBlockNode.id);
  }, [activeBlockNode, edges]);
  const canvasNodes = canvasScope.kind === "block" ? blockCanvasNodes : rootVisibleNodes;
  const canvasEdges = canvasScope.kind === "block" ? blockCanvasEdges : rootVisibleEdges;
  const canvasKey = canvasScope.kind === "block" ? `block-${canvasScope.blockId}` : "root";

  React.useEffect(() => {
    if (
      !pendingFocusNodeId ||
      canvasScope.kind !== "block" ||
      reactFlowCanvas?.key !== canvasKey ||
      !canvasNodes.some((node) => node.id === pendingFocusNodeId)
    ) {
      return;
    }

    const animationFrame = window.requestAnimationFrame(() => {
      const didFitView = reactFlowCanvas.instance.fitView({
        nodes: [{ id: pendingFocusNodeId }],
        padding: 0.5,
        maxZoom: 1,
        duration: 200,
      });
      if (didFitView) {
        setPendingFocusNodeId(null);
      }
    });

    return () => window.cancelAnimationFrame(animationFrame);
  }, [canvasKey, canvasNodes, canvasScope, pendingFocusNodeId, reactFlowCanvas]);
  const selectedNode = React.useMemo(
    () => nodes.find((node) => node.id === selectedNodeId) ?? null,
    [nodes, selectedNodeId],
  );
  const inspectorNode = selectedNode ?? (canvasScope.kind === "block" ? activeBlockNode : null);
  const selectedEdge = React.useMemo(
    () =>
      canvasScope.kind === "root"
        ? (edges.find((edge) => edge.id === selectedEdgeId) ?? null)
        : null,
    [canvasScope, edges, selectedEdgeId],
  );
  const activeBlockMembers = activeBlockNode
    ? (blockMembership.membersByBlockId.get(activeBlockNode.id) ?? [])
    : [];

  const handleAutoLayout = React.useCallback(async () => {
    if (canvasNodes.length === 0) return;
    const result = await createGraphLayout(canvasNodes, canvasEdges);
    const positionByNodeId = new Map(result.nodes.map((node) => [node.id, node.position]));
    setNodes((current) =>
      current.map((node) => {
        const position = positionByNodeId.get(node.id);
        return position ? { ...node, position } : node;
      }),
    );
  }, [canvasEdges, canvasNodes, setNodes]);

  const graphValidation = React.useMemo(() => {
    const validation = validateDag(nodes, edges);
    if (!validation.ok) return validation;

    const outputNodes = nodes.filter((node) => node.data.kind === AgentflowNodeKind.Output);
    const summaryEnabled = outputNodes.some((node) => {
      const config = readConfigJson(node.data.configJson);
      return config !== null && readBoolean(config.enableSummary);
    });
    if (!summaryEnabled) return validation;

    if (outputNodes.length !== 1) {
      return { ok: false, message: "Summary requires exactly one Output node" };
    }

    if (!summaryModelProviderId) {
      return { ok: false, message: "Select a Summary Model Provider" };
    }

    return validation;
  }, [edges, nodes, summaryModelProviderId]);

  const handleBuild = React.useCallback(async () => {
    if (!agentflowName.trim()) {
      toast.error("Please enter an agentflow name");
      return;
    }

    if (nodes.length === 0) {
      toast.error("Please add at least one node");
      return;
    }

    if (!graphValidation.ok) {
      toast.error(graphValidation.message);
      return;
    }

    const requestBody: AgentflowSaveRequest = {
      name: agentflowName,
      description: agentflowDescription || null,
      summaryModelProviderId: summaryModelProviderId || null,
      nodes: nodes.map((node) => ({
        nodeId: node.id,
        kind: node.data.kind,
        relateId: node.data.relateId,
        name: node.data.title || null,
        positionJson: JSON.stringify(node.position),
        instructions: node.data.instructions || null,
        configJson: node.data.configJson || null,
      })),
      edges: edges.map((edge) => {
        const data = { ...createDefaultEdgeData(), ...edge.data };
        return {
          edgeId: edge.id,
          sourceNodeId: edge.source,
          targetNodeId: edge.target,
          kind: data.kind,
          label: data.label || null,
          conditionJson: data.conditionJson || null,
          configJson: data.configJson || null,
        };
      }),
    };

    setIsSaving(true);
    try {
      if (editingAgentflow) {
        await apiPut("/api/agentflows/{id}", {
          params: { path: { id: editingAgentflow.id } },
          body: requestBody,
        });
        toast.success(`Agentflow "${agentflowName}" updated`);
      } else {
        await apiPost("/api/agentflows", { body: requestBody });
        toast.success(`Agentflow "${agentflowName}" created`);
      }

      setAgentflowName("");
      setAgentflowDescription("");
      setSummaryModelProviderId("");
      setNodes([createInputNode<DagNodeData>()]);
      setEdges([]);
      setCanvasScope({ kind: "root" });
      setPendingFocusNodeId(null);
      setSelectedNodeId(INPUT_NODE_ID);
      setSelectedEdgeId(null);
      onAgentflowCreated?.();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Failed to save agentflow");
    } finally {
      setIsSaving(false);
    }
  }, [
    agentflowDescription,
    agentflowName,
    editingAgentflow,
    edges,
    graphValidation,
    nodes,
    onAgentflowCreated,
    setEdges,
    setNodes,
    summaryModelProviderId,
  ]);

  const actionState = React.useMemo<AgentflowBuilderActionState>(
    () => ({
      label: isSaving ? "Saving..." : editingAgentflow ? "Update" : "Create",
      disabled: isSaving || !agentflowName.trim() || !graphValidation.ok,
      isSaving,
      submit: handleBuild,
    }),
    [agentflowName, editingAgentflow, graphValidation.ok, handleBuild, isSaving],
  );

  React.useEffect(() => {
    onActionStateChange?.(actionState);
    return () => onActionStateChange?.(null);
  }, [actionState, onActionStateChange]);

  return (
    <div className="grid h-full min-h-0 grid-cols-[280px_minmax(0,1fr)_340px] gap-4">
      <aside className="min-h-0 overflow-auto rounded-md border bg-muted/20 p-3">
        <div className="space-y-3">
          <div className="space-y-2">
            <Label htmlFor="agentflowName">Agentflow Name *</Label>
            <Input
              id="agentflowName"
              value={agentflowName}
              onChange={(event) => setAgentflowName(event.target.value)}
              placeholder="Release review pipeline"
            />
          </div>

          <div className="space-y-2">
            <Label htmlFor="agentflowDescription">Description</Label>
            <Textarea
              id="agentflowDescription"
              value={agentflowDescription}
              onChange={(event) => setAgentflowDescription(event.target.value)}
              placeholder="Optional notes"
              className="min-h-20"
            />
          </div>
        </div>

        {canvasScope.kind === "block" && activeBlockNode ? (
          <BlockScopePalette
            blockNode={activeBlockNode}
            members={activeBlockMembers}
            agents={agents}
            agentflows={availableAgentflows}
            agentSelectOptions={agentSelectOptions}
            agentflowSelectOptions={agentflowSelectOptions}
            onAddParticipant={addBlockParticipant}
            onSelectParticipant={selectBlockParticipant}
          />
        ) : (
          <RootScopePalette
            agents={agents}
            availableAgentflows={availableAgentflows}
            agentSelectOptions={agentSelectOptions}
            agentflowSelectOptions={agentflowSelectOptions}
            onAddNode={addDagNode}
          />
        )}
      </aside>

      <section className="relative min-h-0 overflow-hidden rounded-md border bg-background">
        <div className="absolute left-3 top-3 z-10 flex items-center gap-2">
          <ScopeBar
            scope={canvasScope}
            activeBlockNode={activeBlockNode}
            validationMessage={graphValidation.message}
            validationOk={graphValidation.ok}
            onExitBlock={exitBlockScope}
          />
        </div>
        <ReactFlow
          key={canvasKey}
          nodes={canvasNodes}
          edges={canvasEdges}
          nodeTypes={nodeTypes}
          onNodesChange={handleNodesChange}
          onEdgesChange={handleEdgesChange}
          onConnect={onConnect}
          onInit={(instance) => setReactFlowCanvas({ key: canvasKey, instance })}
          onSelectionChange={onSelectionChange}
          onNodeClick={(_, node) => {
            setSelectedNodeId(node.id);
            setSelectedEdgeId(null);
          }}
          onNodeDoubleClick={(_, node) => {
            if (isBlockNodeKind(node.data.kind)) {
              openBlockScope(node.id);
            }
          }}
          onEdgeClick={(_, edge) => {
            if (canvasScope.kind === "block") return;
            setSelectedEdgeId(edge.id);
            setSelectedNodeId(null);
          }}
          onPaneClick={() => {
            if (canvasScope.kind === "block") {
              setSelectedNodeId(canvasScope.blockId);
              setSelectedEdgeId(null);
              return;
            }

            setSelectedNodeId(null);
            setSelectedEdgeId(null);
          }}
          nodesConnectable={canvasScope.kind === "root"}
          deleteKeyCode={null}
          fitView
        >
          <Background variant={BackgroundVariant.Dots} gap={18} size={1} />
          <FlowControls onAutoLayout={handleAutoLayout} />
        </ReactFlow>
      </section>

      <aside className="flex min-h-0 flex-col overflow-hidden rounded-md border bg-muted/20">
        <div className="border-b p-3">
          <p className="text-sm font-medium">Inspector</p>
          <p className="text-xs text-muted-foreground">Edit selected node or edge.</p>
        </div>
        <div className="min-h-0 flex-1 overflow-auto p-3">
          {inspectorNode ? (
            <NodeInspector
              node={inspectorNode}
              nodes={nodes}
              agents={agents}
              agentflows={availableAgentflows}
              agentSelectOptions={agentSelectOptions}
              agentflowSelectOptions={agentflowSelectOptions}
              modelProviders={modelProviders}
              summaryModelProviderId={summaryModelProviderId}
              blockMembership={blockMembership}
              canvasScope={canvasScope}
              activeBlockNode={activeBlockNode}
              onChange={updateNodeData}
              onSummaryModelProviderIdChange={setSummaryModelProviderId}
              onAddBlockParticipant={addBlockParticipant}
              onRemoveBlockParticipant={removeBlockParticipant}
              onOpenBlock={openBlockScope}
              onSelectBlockParticipant={selectBlockParticipant}
            />
          ) : selectedEdge ? (
            <EdgeInspector edge={selectedEdge} onChange={updateEdgeData} />
          ) : (
            <div className="rounded-md border border-dashed p-4 text-sm text-muted-foreground">
              Select a node or edge on the canvas.
            </div>
          )}
        </div>
      </aside>
    </div>
  );
}

function PaletteButton({ label, onClick }: { label: string; onClick: () => void }) {
  return (
    <Button type="button" variant="outline" className="w-full justify-start" onClick={onClick}>
      {label}
    </Button>
  );
}

function ScopeBar({
  scope,
  activeBlockNode,
  validationMessage,
  validationOk,
  onExitBlock,
}: {
  scope: CanvasScope;
  activeBlockNode: Node<DagNodeData> | null;
  validationMessage: string;
  validationOk: boolean;
  onExitBlock: () => void;
}) {
  const validationClassName = validationOk
    ? "border-emerald-200 bg-emerald-50 text-emerald-700"
    : "border-destructive/30 bg-destructive/10 text-destructive";

  if (scope.kind === "block" && activeBlockNode) {
    return (
      <div className="flex items-center gap-2 rounded-full border bg-background/95 px-2 py-1 text-xs shadow-sm">
        <Button
          type="button"
          variant="ghost"
          size="icon-sm"
          className="h-6 w-6"
          onClick={onExitBlock}
        >
          <ArrowLeft className="h-3.5 w-3.5" />
        </Button>
        <span className="text-muted-foreground">Agentflow</span>
        <ChevronRight className="h-3 w-3 text-muted-foreground" />
        <span className="max-w-[220px] truncate font-medium">{activeBlockNode.data.title}</span>
        <Badge variant="outline">Block Canvas</Badge>
        <span className={`rounded-full border px-2 py-0.5 ${validationClassName}`}>
          {validationMessage}
        </span>
      </div>
    );
  }

  return (
    <div className={`rounded-full border px-3 py-1 text-xs ${validationClassName}`}>
      {validationMessage}
    </div>
  );
}

function RootScopePalette({
  agents,
  availableAgentflows,
  agentSelectOptions,
  agentflowSelectOptions,
  onAddNode,
}: {
  agents: AgentDto[];
  availableAgentflows: AgentflowDto[];
  agentSelectOptions: SearchableSelectOption[];
  agentflowSelectOptions: SearchableSelectOption[];
  onAddNode: (kind: AgentflowNodeKind, title: string, relateId?: string | null) => void;
}) {
  return (
    <div className="mt-5 space-y-3">
      <div>
        <p className="mb-2 text-xs font-medium uppercase tracking-wide text-muted-foreground">
          Primitive Nodes
        </p>
        <div className="space-y-2">
          <SearchableSelect
            id="agentflow-builder-agent-select"
            ariaLabel="Select agent"
            value=""
            onValueChange={(value) => {
              const agent = agents.find((item) => item.id === value);
              if (agent) onAddNode(AgentflowNodeKind.Agent, agent.name, agent.id);
            }}
            options={agentSelectOptions}
            placeholder="Select agent"
            searchPlaceholder="Search agents..."
            clearable={false}
          />
          <SearchableSelect
            id="agentflow-builder-workflow-select"
            ariaLabel="Select workflow"
            value=""
            onValueChange={(value) => {
              const agentflow = availableAgentflows.find((item) => item.id === value);
              if (agentflow) {
                onAddNode(AgentflowNodeKind.WorkflowAsAgent, agentflow.name, agentflow.id);
              }
            }}
            options={agentflowSelectOptions}
            placeholder="Select workflow"
            searchPlaceholder="Search workflows..."
            clearable={false}
          />
          <PaletteButton
            label="Prompt Adapter"
            onClick={() => onAddNode(AgentflowNodeKind.PromptAdapter, "Prompt Adapter")}
          />
          <PaletteButton
            label="Human Gate"
            onClick={() => onAddNode(AgentflowNodeKind.HumanGate, "Human Gate")}
          />
          <PaletteButton
            label="Checkpoint"
            onClick={() => onAddNode(AgentflowNodeKind.CheckpointMarker, "Checkpoint")}
          />
          <PaletteButton
            label="Output"
            onClick={() => onAddNode(AgentflowNodeKind.Output, "Output")}
          />
        </div>
      </div>

      <div>
        <p className="mb-2 text-xs font-medium uppercase tracking-wide text-muted-foreground">
          Orchestration Blocks
        </p>
        <div className="space-y-2">
          <PaletteButton
            label="Concurrent Block"
            onClick={() => onAddNode(AgentflowNodeKind.ConcurrentBlock, "Concurrent Block")}
          />
          <PaletteButton
            label="Handoff Group"
            onClick={() => onAddNode(AgentflowNodeKind.HandoffBlock, "Handoff Group")}
          />
          <PaletteButton
            label="GroupChat Room"
            onClick={() => onAddNode(AgentflowNodeKind.GroupChatBlock, "GroupChat Room")}
          />
          <PaletteButton
            label="Magentic Team"
            onClick={() => onAddNode(AgentflowNodeKind.MagenticBlock, "Magentic Team")}
          />
        </div>
      </div>
    </div>
  );
}

function BlockScopePalette({
  blockNode,
  members,
  agents,
  agentflows,
  agentSelectOptions,
  agentflowSelectOptions,
  onAddParticipant,
  onSelectParticipant,
}: {
  blockNode: Node<DagNodeData>;
  members: BlockMemberView[];
  agents: AgentDto[];
  agentflows: AgentflowDto[];
  agentSelectOptions: SearchableSelectOption[];
  agentflowSelectOptions: SearchableSelectOption[];
  onAddParticipant: (
    blockId: string,
    kind: AgentflowNodeKind,
    title: string,
    relateId: string,
  ) => void;
  onSelectParticipant: (blockId: string, participantNodeId: string) => void;
}) {
  return (
    <div className="mt-5 space-y-4">
      <div className="rounded-md border bg-background p-3">
        <p className="truncate text-sm font-medium">{blockNode.data.title}</p>
        <p className="mt-1 text-xs text-muted-foreground">{NODE_META[blockNode.data.kind].label}</p>
      </div>

      <BlockMemberAddControls
        idPrefix={`block-scope-${blockNode.id}`}
        blockId={blockNode.id}
        agents={agents}
        agentflows={agentflows}
        agentSelectOptions={agentSelectOptions}
        agentflowSelectOptions={agentflowSelectOptions}
        onAddParticipant={onAddParticipant}
      />

      <div>
        <p className="mb-2 text-xs font-medium uppercase tracking-wide text-muted-foreground">
          Members
        </p>
        {members.length === 0 ? (
          <div className="rounded-md border border-dashed p-3 text-xs text-muted-foreground">
            No members.
          </div>
        ) : (
          <div className="space-y-2">
            {members.map((member) => (
              <button
                key={member.nodeId}
                type="button"
                className="w-full rounded-md border bg-background p-2 text-left text-xs transition-colors hover:border-primary/40 hover:bg-primary/5"
                onClick={() => onSelectParticipant(blockNode.id, member.nodeId)}
              >
                <div className="flex items-center justify-between gap-2">
                  <span className="truncate font-medium">{member.title}</span>
                  <Badge variant="outline">{getNodeKindLabel(member.kind)}</Badge>
                </div>
                <BlockMemberBadges member={member} />
              </button>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}

function BlockMemberAddControls({
  idPrefix,
  blockId,
  agents,
  agentflows,
  agentSelectOptions,
  agentflowSelectOptions,
  onAddParticipant,
}: {
  idPrefix: string;
  blockId: string;
  agents: AgentDto[];
  agentflows: AgentflowDto[];
  agentSelectOptions: SearchableSelectOption[];
  agentflowSelectOptions: SearchableSelectOption[];
  onAddParticipant: (
    blockId: string,
    kind: AgentflowNodeKind,
    title: string,
    relateId: string,
  ) => void;
}) {
  return (
    <div className="space-y-2">
      <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
        Add Member
      </p>
      <SearchableSelect
        id={`${idPrefix}-agent-select`}
        ariaLabel="Add agent member"
        value=""
        onValueChange={(value) => {
          const agent = agents.find((item) => item.id === value);
          if (agent) onAddParticipant(blockId, AgentflowNodeKind.Agent, agent.name, agent.id);
        }}
        options={agentSelectOptions}
        placeholder="Add agent"
        searchPlaceholder="Search agents..."
        clearable={false}
      />
      <SearchableSelect
        id={`${idPrefix}-workflow-select`}
        ariaLabel="Add workflow member"
        value=""
        onValueChange={(value) => {
          const agentflow = agentflows.find((item) => item.id === value);
          if (agentflow) {
            onAddParticipant(
              blockId,
              AgentflowNodeKind.WorkflowAsAgent,
              agentflow.name,
              agentflow.id,
            );
          }
        }}
        options={agentflowSelectOptions}
        placeholder="Add workflow"
        searchPlaceholder="Search workflows..."
        clearable={false}
      />
    </div>
  );
}

function NodeInspector({
  node,
  nodes,
  agents,
  agentflows,
  agentSelectOptions,
  agentflowSelectOptions,
  modelProviders,
  summaryModelProviderId,
  blockMembership,
  canvasScope,
  activeBlockNode,
  onChange,
  onSummaryModelProviderIdChange,
  onAddBlockParticipant,
  onRemoveBlockParticipant,
  onOpenBlock,
  onSelectBlockParticipant,
}: {
  node: Node<DagNodeData>;
  nodes: Node<DagNodeData>[];
  agents: AgentDto[];
  agentflows: AgentflowDto[];
  agentSelectOptions: SearchableSelectOption[];
  agentflowSelectOptions: SearchableSelectOption[];
  modelProviders: ModelProviderDto[];
  summaryModelProviderId: string;
  blockMembership: BlockMembership;
  canvasScope: CanvasScope;
  activeBlockNode: Node<DagNodeData> | null;
  onChange: (nodeId: string, update: Partial<DagNodeData>) => void;
  onSummaryModelProviderIdChange: (value: string) => void;
  onAddBlockParticipant: (
    blockId: string,
    kind: AgentflowNodeKind,
    title: string,
    relateId: string,
  ) => void;
  onRemoveBlockParticipant: (blockId: string, participantNodeId: string) => void;
  onOpenBlock: (blockId: string) => void;
  onSelectBlockParticipant: (blockId: string, participantNodeId: string) => void;
}) {
  const meta = NODE_META[node.data.kind];
  const configIsInvalid =
    node.data.configJson.trim().length > 0 && readConfigJson(node.data.configJson) === null;
  if (node.data.kind === AgentflowNodeKind.Input) {
    return (
      <div className="space-y-3">
        <div className="rounded-md border bg-background p-3">
          <div className="flex items-center justify-between gap-2">
            <p className="text-sm font-medium">{meta.label}</p>
            <Badge variant="outline">System</Badge>
          </div>
          <p className="mt-1 text-xs text-muted-foreground">{meta.body}</p>
        </div>
        <div className="rounded-md border border-dashed p-3 text-xs text-muted-foreground">
          Input is the fixed user-input start node. It can fan out to downstream nodes and cannot be
          deleted or used as a downstream target.
        </div>
      </div>
    );
  }

  const usesInstructions =
    node.data.kind !== AgentflowNodeKind.Output &&
    node.data.kind !== AgentflowNodeKind.HumanGate &&
    node.data.kind !== AgentflowNodeKind.CheckpointMarker;

  return (
    <div className="space-y-3">
      <div className="rounded-md border bg-background p-3">
        <div className="flex items-center justify-between gap-2">
          <p className="text-sm font-medium">{meta.label}</p>
          {configIsInvalid ? <Badge variant="destructive">Invalid JSON</Badge> : null}
        </div>
        <p className="mt-1 text-xs text-muted-foreground">{meta.body}</p>
      </div>
      <div className="space-y-2">
        <Label>Name</Label>
        <Input
          value={node.data.title}
          onChange={(event) => onChange(node.id, { title: event.target.value })}
        />
      </div>

      {usesInstructions ? (
        <div className="space-y-2">
          <Label>System Prompt / Instructions</Label>
          <Textarea
            value={node.data.instructions}
            onChange={(event) => onChange(node.id, { instructions: event.target.value })}
            placeholder="Describe how this node should use upstream output."
            className="min-h-28"
          />
        </div>
      ) : null}

      {isBlockNodeKind(node.data.kind) ? (
        <BlockConfigInspector
          node={node}
          nodes={nodes}
          agents={agents}
          agentflows={agentflows}
          agentSelectOptions={agentSelectOptions}
          agentflowSelectOptions={agentflowSelectOptions}
          blockMembership={blockMembership}
          canvasScope={canvasScope}
          onChange={onChange}
          onAddParticipant={onAddBlockParticipant}
          onRemoveParticipant={onRemoveBlockParticipant}
          onOpenBlock={onOpenBlock}
          onSelectParticipant={onSelectBlockParticipant}
        />
      ) : null}

      {canvasScope.kind === "block" && activeBlockNode && isAgentParticipantKind(node.data.kind) ? (
        <BlockParticipantContextInspector
          blockNode={activeBlockNode}
          memberNode={node}
          blockMembership={blockMembership}
          onChange={onChange}
          onRemoveParticipant={onRemoveBlockParticipant}
        />
      ) : null}

      {node.data.kind === AgentflowNodeKind.HumanGate ? (
        <HumanGateConfigInspector node={node} onChange={onChange} />
      ) : null}

      {node.data.kind === AgentflowNodeKind.CheckpointMarker ? (
        <CheckpointConfigInspector node={node} onChange={onChange} />
      ) : null}

      {node.data.kind === AgentflowNodeKind.Output ? (
        <OutputSummaryConfigInspector
          node={node}
          modelProviders={modelProviders}
          summaryModelProviderId={summaryModelProviderId}
          onChange={onChange}
          onSummaryModelProviderIdChange={onSummaryModelProviderIdChange}
        />
      ) : null}

      <div className="space-y-2">
        <Label>Advanced Config JSON</Label>
        <Textarea
          value={node.data.configJson}
          onChange={(event) => onChange(node.id, { configJson: event.target.value })}
          placeholder='{ "key": "value" }'
          className="min-h-24 font-mono text-xs"
        />
      </div>
    </div>
  );
}

function OutputSummaryConfigInspector({
  node,
  modelProviders,
  summaryModelProviderId,
  onChange,
  onSummaryModelProviderIdChange,
}: {
  node: Node<DagNodeData>;
  modelProviders: ModelProviderDto[];
  summaryModelProviderId: string;
  onChange: (nodeId: string, update: Partial<DagNodeData>) => void;
  onSummaryModelProviderIdChange: (value: string) => void;
}) {
  const config = readConfigJson(node.data.configJson) ?? {};
  const summaryEnabled = readBoolean(config.enableSummary);
  const setConfig = (update: Record<string, unknown>) => {
    onChange(node.id, { configJson: updateConfigJson(node.data.configJson, update) });
  };

  return (
    <div className="space-y-3 rounded-md border bg-background p-3">
      <div className="flex items-start justify-between gap-3">
        <div className="space-y-1">
          <Label htmlFor={`output-summary-${node.id}`} className="cursor-pointer">
            Generate Summary
          </Label>
          <p className="text-xs text-muted-foreground">
            Append a Markdown summary from the messages entering this Output node.
          </p>
        </div>
        <Switch
          id={`output-summary-${node.id}`}
          checked={summaryEnabled}
          onCheckedChange={(enabled) => setConfig({ enableSummary: enabled })}
        />
      </div>

      {summaryEnabled ? (
        <div className="space-y-2">
          <Label>Summary Model Provider</Label>
          <Select value={summaryModelProviderId} onValueChange={onSummaryModelProviderIdChange}>
            <SelectTrigger className="w-full">
              <SelectValue placeholder="Select a model provider..." />
            </SelectTrigger>
            <SelectContent>
              {modelProviders.length > 0 ? (
                modelProviders.map((modelProvider) => (
                  <SelectItem key={modelProvider.id} value={modelProvider.id}>
                    {modelProvider.modelName} ({modelProvider.providerName}-
                    {modelProvider.providerType})
                  </SelectItem>
                ))
              ) : (
                <SelectItem value="no-providers" disabled>
                  No model providers available
                </SelectItem>
              )}
            </SelectContent>
          </Select>
        </div>
      ) : null}
    </div>
  );
}

function BlockConfigInspector({
  node,
  nodes,
  agents,
  agentflows,
  agentSelectOptions,
  agentflowSelectOptions,
  blockMembership,
  canvasScope,
  onChange,
  onAddParticipant,
  onRemoveParticipant,
  onOpenBlock,
  onSelectParticipant,
}: {
  node: Node<DagNodeData>;
  nodes: Node<DagNodeData>[];
  agents: AgentDto[];
  agentflows: AgentflowDto[];
  agentSelectOptions: SearchableSelectOption[];
  agentflowSelectOptions: SearchableSelectOption[];
  blockMembership: BlockMembership;
  canvasScope: CanvasScope;
  onChange: (nodeId: string, update: Partial<DagNodeData>) => void;
  onAddParticipant: (
    blockId: string,
    kind: AgentflowNodeKind,
    title: string,
    relateId: string,
  ) => void;
  onRemoveParticipant: (blockId: string, participantNodeId: string) => void;
  onOpenBlock: (blockId: string) => void;
  onSelectParticipant: (blockId: string, participantNodeId: string) => void;
}) {
  const config = readConfigJson(node.data.configJson) ?? {};
  const nodeById = React.useMemo(() => new Map(nodes.map((item) => [item.id, item])), [nodes]);
  const members = blockMembership.membersByBlockId.get(node.id) ?? [];
  const selectedParticipants = members
    .map((member) => nodeById.get(member.nodeId))
    .filter((item): item is Node<DagNodeData> =>
      Boolean(item && isAgentParticipantKind(item.data.kind)),
    );
  const managerNodeId = readString(config.managerNodeId);
  const managerSelectValue = selectedParticipants.some(
    (participant) => participant.id === managerNodeId,
  )
    ? managerNodeId
    : "";

  const setConfig = (update: Record<string, unknown>) => {
    onChange(node.id, { configJson: updateConfigJson(node.data.configJson, update) });
  };

  return (
    <div className="space-y-3 rounded-md border bg-background p-3">
      <div className="flex items-center justify-between gap-2">
        <Label>Members</Label>
        <div className="flex items-center gap-2">
          <Badge variant="outline">{members.length} total</Badge>
          <Button
            type="button"
            variant="outline"
            size="sm"
            className="h-7"
            onClick={() => onOpenBlock(node.id)}
          >
            <ExternalLink className="h-3.5 w-3.5" />
            Open
          </Button>
        </div>
      </div>

      <BlockMemberAddControls
        idPrefix={`block-inspector-${node.id}`}
        blockId={node.id}
        agents={agents}
        agentflows={agentflows}
        agentSelectOptions={agentSelectOptions}
        agentflowSelectOptions={agentflowSelectOptions}
        onAddParticipant={onAddParticipant}
      />

      {members.length === 0 ? (
        <div className="rounded-md border border-dashed p-3 text-xs text-muted-foreground">
          No members.
        </div>
      ) : (
        <div className="space-y-2">
          {members.map((member) => (
            <BlockMemberListItem
              key={member.nodeId}
              blockId={node.id}
              member={member}
              memberNode={nodeById.get(member.nodeId) ?? null}
              isCurrentBlockScope={canvasScope.kind === "block" && canvasScope.blockId === node.id}
              onSelect={onSelectParticipant}
              onRemove={onRemoveParticipant}
            />
          ))}
        </div>
      )}

      {node.data.kind === AgentflowNodeKind.GroupChatBlock ||
      node.data.kind === AgentflowNodeKind.MagenticBlock ? (
        <ConfigNumberField
          label="Max Rounds"
          value={readNumber(config.maxRounds)}
          onChange={(value) => setConfig({ maxRounds: value })}
        />
      ) : null}

      {node.data.kind === AgentflowNodeKind.MagenticBlock ? (
        <div className="space-y-3">
          <div className="space-y-2">
            <Label>Manager</Label>
            <Select
              value={managerSelectValue}
              onValueChange={(value) => setConfig({ managerNodeId: value })}
              disabled={selectedParticipants.length === 0}
            >
              <SelectTrigger>
                <SelectValue placeholder="First participant" />
              </SelectTrigger>
              <SelectContent>
                {selectedParticipants.map((participant) => (
                  <SelectItem key={participant.id} value={participant.id}>
                    {participant.data.title}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
          <div className="grid grid-cols-2 gap-2">
            <ConfigNumberField
              label="Max Stalls"
              value={readNumber(config.maxStalls)}
              onChange={(value) => setConfig({ maxStalls: value })}
            />
            <ConfigNumberField
              label="Max Resets"
              value={readNumber(config.maxResets)}
              onChange={(value) => setConfig({ maxResets: value })}
            />
          </div>
          <div className="flex items-center justify-between rounded-md border px-3 py-2">
            <Label className="cursor-pointer">Require Plan Signoff</Label>
            <Switch
              checked={readBoolean(config.requirePlanSignoff)}
              onCheckedChange={(checked) => setConfig({ requirePlanSignoff: checked })}
            />
          </div>
        </div>
      ) : null}

      {node.data.kind === AgentflowNodeKind.HandoffBlock ? (
        <div className="space-y-3">
          <div className="space-y-2">
            <Label>Handoff Instructions</Label>
            <Textarea
              value={readString(config.handoffInstructions)}
              onChange={(event) => setConfig({ handoffInstructions: event.target.value })}
              placeholder="Describe when one participant should hand off to another."
              className="min-h-24"
            />
          </div>
          <div className="flex items-center justify-between rounded-md border px-3 py-2">
            <Label className="cursor-pointer">Return To Previous</Label>
            <Switch
              checked={readBoolean(config.enableReturnToPrevious)}
              onCheckedChange={(checked) => setConfig({ enableReturnToPrevious: checked })}
            />
          </div>
          <div className="flex items-center justify-between rounded-md border px-3 py-2">
            <Label className="cursor-pointer">Autonomous Mode</Label>
            <Switch
              checked={readBoolean(config.autonomous)}
              onCheckedChange={(checked) => setConfig({ autonomous: checked })}
            />
          </div>
          {readBoolean(config.autonomous) ? (
            <>
              <ConfigNumberField
                label="Autonomous Turn Limit"
                value={readNumber(config.autonomousTurnLimit)}
                onChange={(value) => setConfig({ autonomousTurnLimit: value })}
              />
              <div className="space-y-2">
                <Label>Continuation Prompt</Label>
                <Textarea
                  value={readString(config.continuationPrompt)}
                  onChange={(event) => setConfig({ continuationPrompt: event.target.value })}
                  className="min-h-20"
                />
              </div>
            </>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}

function BlockMemberListItem({
  blockId,
  member,
  memberNode,
  isCurrentBlockScope,
  onSelect,
  onRemove,
}: {
  blockId: string;
  member: BlockMemberView;
  memberNode: Node<DagNodeData> | null;
  isCurrentBlockScope: boolean;
  onSelect: (blockId: string, participantNodeId: string) => void;
  onRemove: (blockId: string, participantNodeId: string) => void;
}) {
  return (
    <div
      className={`flex items-start gap-2 rounded-md border p-2 cursor-pointer ${
        isCurrentBlockScope ? "bg-primary/5" : ""
      }`}
    >
      <button
        type="button"
        disabled={!memberNode}
        className="cursor-pointer min-w-0 flex-1 text-left disabled:cursor-not-allowed disabled:opacity-60"
        onClick={() => onSelect(blockId, member.nodeId)}
      >
        <div className="flex items-center justify-between gap-2">
          <span className="truncate text-sm font-medium">{member.title}</span>
          <Badge variant="outline">{getNodeKindLabel(member.kind)}</Badge>
        </div>
        <BlockMemberBadges member={member} />
      </button>
      <Button
        type="button"
        variant="ghost"
        size="icon-sm"
        title="Remove from block"
        onClick={() => onRemove(blockId, member.nodeId)}
      >
        <X className="h-4 w-4" />
      </Button>
    </div>
  );
}

function BlockParticipantContextInspector({
  blockNode,
  memberNode,
  blockMembership,
  onChange,
  onRemoveParticipant,
}: {
  blockNode: Node<DagNodeData>;
  memberNode: Node<DagNodeData>;
  blockMembership: BlockMembership;
  onChange: (nodeId: string, update: Partial<DagNodeData>) => void;
  onRemoveParticipant: (blockId: string, participantNodeId: string) => void;
}) {
  const config = readConfigJson(blockNode.data.configJson) ?? {};
  const member = blockMembership.membersByBlockId
    .get(blockNode.id)
    ?.find((item) => item.nodeId === memberNode.id);
  const isMagentic = blockNode.data.kind === AgentflowNodeKind.MagenticBlock;
  const isManager = readString(config.managerNodeId) === memberNode.id;

  return (
    <div className="space-y-3 rounded-md border bg-background p-3">
      <div className="flex items-center justify-between gap-2">
        <div className="min-w-0">
          <p className="truncate text-sm font-medium">{blockNode.data.title}</p>
          <p className="text-xs text-muted-foreground">{NODE_META[blockNode.data.kind].label}</p>
        </div>
        <Badge variant="outline">Member</Badge>
      </div>

      {member ? <BlockMemberBadges member={member} /> : null}

      <div className="flex flex-wrap gap-2">
        {isMagentic ? (
          <Button
            type="button"
            variant={isManager ? "secondary" : "outline"}
            size="sm"
            disabled={isManager}
            onClick={() =>
              onChange(blockNode.id, {
                configJson: updateConfigJson(blockNode.data.configJson, {
                  managerNodeId: memberNode.id,
                }),
              })
            }
          >
            <Crown className="h-4 w-4" />
            Manager
          </Button>
        ) : null}
        <Button
          type="button"
          variant="outline"
          size="sm"
          onClick={() => onRemoveParticipant(blockNode.id, memberNode.id)}
        >
          <X className="h-4 w-4" />
          Remove
        </Button>
      </div>
    </div>
  );
}

function BlockMemberBadges({ member }: { member: BlockMemberView }) {
  if (!member.isManager && !member.isExternallyLinked && !member.isShared && !member.isMissing) {
    return null;
  }

  return (
    <div className="mt-2 flex flex-wrap gap-1">
      {member.isManager ? <Badge variant="secondary">Manager</Badge> : null}
      {member.isExternallyLinked ? (
        <Badge variant="outline" className="border-amber-200 bg-amber-50 text-amber-800">
          On Canvas
        </Badge>
      ) : null}
      {member.isShared ? (
        <Badge variant="outline" className="border-amber-200 bg-amber-50 text-amber-800">
          Shared
        </Badge>
      ) : null}
      {member.isMissing ? <Badge variant="destructive">Missing</Badge> : null}
    </div>
  );
}

function getNodeKindLabel(kind: number | null) {
  if (kind === null || !Object.hasOwn(NODE_META, kind)) {
    return "Missing";
  }

  return NODE_META[kind as AgentflowNodeKind].label;
}

function HumanGateConfigInspector({
  node,
  onChange,
}: {
  node: Node<DagNodeData>;
  onChange: (nodeId: string, update: Partial<DagNodeData>) => void;
}) {
  const config = readConfigJson(node.data.configJson) ?? {};
  const setConfig = (update: Record<string, unknown>) => {
    onChange(node.id, { configJson: updateConfigJson(node.data.configJson, update) });
  };

  return (
    <div className="space-y-3 rounded-md border bg-background p-3">
      <div className="space-y-2">
        <Label>Human Step Mode</Label>
        <Select
          value={readString(config.humanMode) || "input"}
          onValueChange={(value) => setConfig({ humanMode: value })}
        >
          <SelectTrigger>
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="input">Input</SelectItem>
            <SelectItem value="approval">Approval</SelectItem>
          </SelectContent>
        </Select>
      </div>
      <div className="space-y-2">
        <Label>Human Prompt</Label>
        <Textarea
          value={readString(config.humanPrompt)}
          onChange={(event) => setConfig({ humanPrompt: event.target.value })}
          placeholder="What should the reviewer decide or provide?"
          className="min-h-24"
        />
      </div>
    </div>
  );
}

function CheckpointConfigInspector({
  node,
  onChange,
}: {
  node: Node<DagNodeData>;
  onChange: (nodeId: string, update: Partial<DagNodeData>) => void;
}) {
  const config = readConfigJson(node.data.configJson) ?? {};
  const setConfig = (update: Record<string, unknown>) => {
    onChange(node.id, { configJson: updateConfigJson(node.data.configJson, update) });
  };

  return (
    <div className="space-y-2 rounded-md border bg-background p-3">
      <Label>Checkpoint Name</Label>
      <Input
        value={readString(config.checkpointName)}
        onChange={(event) => setConfig({ checkpointName: event.target.value })}
        placeholder={node.data.title}
      />
    </div>
  );
}

function ConfigNumberField({
  label,
  value,
  onChange,
}: {
  label: string;
  value: number | undefined;
  onChange: (value: number | undefined) => void;
}) {
  return (
    <div className="space-y-2">
      <Label>{label}</Label>
      <Input
        type="number"
        min={1}
        value={value ?? ""}
        onChange={(event) => onChange(parseOptionalInteger(event.target.value))}
      />
    </div>
  );
}

function EdgeInspector({
  edge,
  onChange,
}: {
  edge: Edge<DagEdgeData>;
  onChange: (edgeId: string, update: Partial<DagEdgeData>) => void;
}) {
  const data = { ...createDefaultEdgeData(), ...edge.data };
  const isInputSource = edge.source === INPUT_NODE_ID;

  return (
    <div className="space-y-3">
      <div className="rounded-md border bg-background p-3">
        <p className="text-sm font-medium">{EDGE_LABELS[data.kind]}</p>
        <p className="mt-1 text-xs text-muted-foreground">
          {edge.source} {"->"} {edge.target}
        </p>
        <p className="mt-2 text-xs text-muted-foreground">{EDGE_HELP_TEXT[data.kind]}</p>
      </div>
      <div className="space-y-2">
        <Label>Edge Type</Label>
        <Select
          value={String(data.kind)}
          disabled={isInputSource}
          onValueChange={(value) => {
            if (isInputSource) return;

            const kind = Number(value) as AgentflowEdgeKind;
            onChange(edge.id, {
              kind,
              conditionJson: kind === AgentflowEdgeKind.Direct ? data.conditionJson : "",
            });
          }}
        >
          <SelectTrigger>
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {Object.entries(EDGE_LABELS).map(([value, label]) => (
              <SelectItem key={value} value={value}>
                {label}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </div>
      <div className="space-y-2">
        <Label>Label</Label>
        <Input
          value={data.label}
          onChange={(event) => onChange(edge.id, { label: event.target.value })}
        />
      </div>
      {data.kind === AgentflowEdgeKind.Direct ? (
        <div className="space-y-2">
          <Label>Predicate JSON</Label>
          <Textarea
            value={data.conditionJson}
            onChange={(event) => onChange(edge.id, { conditionJson: event.target.value })}
            placeholder='{ "contains": "approved", "minMessages": 1 }'
            className="min-h-28 font-mono text-xs"
          />
          <p className="text-xs text-muted-foreground">
            Optional keys: always, contains, notContains, equals, author, role, minMessages.
          </p>
        </div>
      ) : (
        <div className="rounded-md border bg-background p-3 text-xs text-muted-foreground">
          Fan edges are structural MAF edges and do not use predicate JSON.
        </div>
      )}
      <div className="space-y-2">
        <Label>Advanced Config JSON</Label>
        <Textarea
          value={data.configJson}
          onChange={(event) => onChange(edge.id, { configJson: event.target.value })}
          className="min-h-20 font-mono text-xs"
        />
      </div>
    </div>
  );
}

function createDefaultEdgeData(kind: AgentflowEdgeKind = AgentflowEdgeKind.Direct): DagEdgeData {
  return {
    kind,
    label: "",
    conditionJson: "",
    configJson: "",
  };
}

function createFlowEdge(edge: AgentflowEdgeDto): Edge<DagEdgeData> {
  return applyEdgeVisuals({
    id: edge.edgeId,
    source: edge.sourceNodeId,
    target: edge.targetNodeId,
    label: edge.label || undefined,
    data: {
      kind: edge.kind,
      label: edge.label || "",
      conditionJson: edge.conditionJson || "",
      configJson: edge.configJson || "",
    },
  });
}

function applyEdgeVisuals(edge: Edge<DagEdgeData>): Edge<DagEdgeData> {
  const data = { ...createDefaultEdgeData(), ...edge.data };
  const visual = getEdgeVisual(data.kind);

  return {
    ...edge,
    animated: visual.animated,
    markerEnd: { type: MarkerType.ArrowClosed, color: visual.color },
    style: { stroke: visual.color, strokeWidth: visual.width },
    label: data.label || undefined,
    data,
  };
}

function getEdgeVisual(kind: AgentflowEdgeKind): {
  color: string;
  width: number;
  animated: boolean;
} {
  if (kind === AgentflowEdgeKind.FanOut) {
    return { color: "#2563eb", width: 2, animated: true };
  }

  if (kind === AgentflowEdgeKind.FanIn) {
    return { color: "#d97706", width: 2, animated: false };
  }

  return { color: "#475569", width: 1.75, animated: false };
}

function parsePosition(positionJson: string | null, index: number) {
  if (!positionJson) {
    return { x: 160 + index * 80, y: 120 + index * 70 };
  }

  try {
    const parsed = JSON.parse(positionJson) as { x?: unknown; y?: unknown };
    if (typeof parsed.x === "number" && typeof parsed.y === "number") {
      return { x: parsed.x, y: parsed.y };
    }
  } catch {
    // Fall through to deterministic default.
  }

  return { x: 160 + index * 80, y: 120 + index * 70 };
}

function resolveNodeTitle(node: AgentflowNodeDto, agents: AgentDto[], agentflows: AgentflowDto[]) {
  if (node.kind === AgentflowNodeKind.Agent && node.relateId) {
    return agents.find((agent) => agent.id === node.relateId)?.name || "Unknown Agent";
  }

  if (node.kind === AgentflowNodeKind.WorkflowAsAgent && node.relateId) {
    return (
      agentflows.find((agentflow) => agentflow.id === node.relateId)?.name || "Unknown Workflow"
    );
  }

  return NODE_META[node.kind]?.label || "Node";
}

function validateDag(nodes: Node<DagNodeData>[], edges: Edge<DagEdgeData>[]) {
  if (nodes.length === 0) {
    return { ok: false, message: "Add at least one node" };
  }

  const inputValidation = validateInputGraph(nodes, edges);
  if (!inputValidation.ok) {
    return inputValidation;
  }

  if (!nodes.some((node) => isAgentParticipantKind(node.data.kind))) {
    return { ok: false, message: "Add at least one Agent or Workflow-as-Agent node" };
  }

  const nodeIds = new Set(nodes.map((node) => node.id));
  if (edges.some((edge) => !nodeIds.has(edge.source) || !nodeIds.has(edge.target))) {
    return { ok: false, message: "Edge references a missing node" };
  }

  for (const node of nodes) {
    if (
      (node.data.kind === AgentflowNodeKind.Agent ||
        node.data.kind === AgentflowNodeKind.WorkflowAsAgent) &&
      !node.data.relateId
    ) {
      return { ok: false, message: `${node.data.title || node.id} needs a linked runtime` };
    }

    const config = readConfigJson(node.data.configJson);
    if (config === null) {
      return { ok: false, message: `${node.data.title || node.id} has invalid config JSON` };
    }

    if (isBlockNodeKind(node.data.kind)) {
      const participantNodeIds = readStringArray(config.participantNodeIds);
      if (participantNodeIds.length === 0) {
        return { ok: false, message: `${node.data.title || node.id} needs participants` };
      }

      if (
        (node.data.kind === AgentflowNodeKind.HandoffBlock ||
          node.data.kind === AgentflowNodeKind.GroupChatBlock ||
          node.data.kind === AgentflowNodeKind.MagenticBlock) &&
        participantNodeIds.length < 2
      ) {
        return {
          ok: false,
          message: `${node.data.title || node.id} needs at least two participants`,
        };
      }

      for (const participantId of participantNodeIds) {
        const participant = nodes.find((item) => item.id === participantId);
        if (!participant || !isAgentParticipantKind(participant.data.kind)) {
          return { ok: false, message: `${node.data.title || node.id} has an invalid participant` };
        }
      }

      const managerNodeId = readString(config.managerNodeId);
      if (
        node.data.kind === AgentflowNodeKind.MagenticBlock &&
        managerNodeId &&
        !participantNodeIds.includes(managerNodeId)
      ) {
        return {
          ok: false,
          message: `${node.data.title || node.id} manager must be a participant`,
        };
      }
    }
  }

  for (const edge of edges) {
    const data = { ...createDefaultEdgeData(), ...edge.data };
    if (data.conditionJson.trim()) {
      const conditionError = validateConditionJson(data.conditionJson);
      if (conditionError) {
        return { ok: false, message: conditionError };
      }
    }

    if (data.configJson.trim() && !isJsonObject(data.configJson)) {
      return { ok: false, message: `${data.label || edge.id} has invalid edge config JSON` };
    }
  }

  const adjacency = new Map(nodes.map((node) => [node.id, [] as string[]]));
  edges.forEach((edge) => adjacency.get(edge.source)?.push(edge.target));

  const visiting = new Set<string>();
  const visited = new Set<string>();

  const hasCycle = (nodeId: string): boolean => {
    if (visiting.has(nodeId)) return true;
    if (visited.has(nodeId)) return false;
    visiting.add(nodeId);
    for (const next of adjacency.get(nodeId) || []) {
      if (hasCycle(next)) return true;
    }
    visiting.delete(nodeId);
    visited.add(nodeId);
    return false;
  };

  if (nodes.some((node) => hasCycle(node.id))) {
    return { ok: false, message: "DAG cannot contain cycles" };
  }

  return { ok: true, message: `Valid DAG · ${nodes.length} nodes · ${edges.length} edges` };
}

function isJsonObject(value: string) {
  if (!value.trim()) return false;
  try {
    const parsed = JSON.parse(value);
    return typeof parsed === "object" && parsed !== null && !Array.isArray(parsed);
  } catch {
    return false;
  }
}

function validateConditionJson(value: string): string | null {
  if (!value.trim()) return null;

  try {
    const parsed = JSON.parse(value) as unknown;
    if (!isPlainObject(parsed)) {
      return "Predicate JSON must be an object";
    }

    for (const key of Object.keys(parsed)) {
      if (!CONDITION_KEYS.has(key)) {
        return `Predicate JSON has unsupported key "${key}"`;
      }
    }

    return null;
  } catch {
    return "Predicate JSON is invalid";
  }
}

function readConfigJson(value: string): Record<string, unknown> | null {
  if (!value.trim()) return {};

  try {
    const parsed = JSON.parse(value) as unknown;
    return isPlainObject(parsed) ? parsed : null;
  } catch {
    return null;
  }
}

function updateConfigJson(currentJson: string, update: Record<string, unknown>) {
  const config = readConfigJson(currentJson) ?? {};
  for (const [key, value] of Object.entries(update)) {
    if (shouldRemoveConfigValue(value)) {
      delete config[key];
    } else {
      config[key] = value;
    }
  }

  return Object.keys(config).length > 0 ? JSON.stringify(config, null, 2) : "";
}

function shouldRemoveConfigValue(value: unknown) {
  if (value === undefined || value === null) return true;
  if (typeof value === "string" && value.trim() === "") return true;
  if (Array.isArray(value) && value.length === 0) return true;
  return false;
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function readString(value: unknown) {
  return typeof value === "string" ? value : "";
}

function readNumber(value: unknown) {
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

function readBoolean(value: unknown) {
  return typeof value === "boolean" ? value : false;
}

function readStringArray(value: unknown) {
  return Array.isArray(value)
    ? value.filter((item): item is string => typeof item === "string")
    : [];
}

function parseOptionalInteger(value: string) {
  if (!value.trim()) return undefined;

  const parsed = Number(value);
  if (!Number.isFinite(parsed)) return undefined;
  return Math.max(1, Math.trunc(parsed));
}
