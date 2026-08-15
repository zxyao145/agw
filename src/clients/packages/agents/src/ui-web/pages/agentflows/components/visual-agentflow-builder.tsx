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
  applyEdgeChanges,
  applyNodeChanges,
  addEdge,
  useReactFlow,
} from "reactflow";
import "reactflow/dist/style.css";

import { apiPost, apiPut } from "@agw/api";
import { SearchableSelect, type SearchableSelectOption } from "@agw/components";
import { Badge } from "@agw/components";
import { Button } from "@agw/components";
import { Card, CardContent, CardHeader, CardTitle } from "@agw/components";
import { Input } from "@agw/components";
import { Label } from "@agw/components";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@agw/components";
import { Switch } from "@agw/components";
import { Textarea } from "@agw/components";
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
} from "../../../../types/agentflow";
import {
  ArrowDown,
  ArrowLeft,
  ArrowUp,
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
  createDefaultEdgeData,
  getDefaultEdgeKindForSource,
  getEdgeRoutingLabel,
  getNextSwitchCaseOrder,
  getSwitchCasePosition,
  isPredicateEdgeKind,
  moveSwitchCaseEdge,
  normalizeSwitchCaseOrders,
  removeAgentflowEdge,
  setSwitchCaseOrder,
  validateAgentflowEdgeRouting,
} from "./agentflow-edge-routing";
import {
  createInputNode,
  ensureInputGraph,
  INPUT_NODE_ID,
  validateInputGraph,
} from "./agentflow-input-node";
import { validateAgentflowCycles } from "./agentflow-cycle-validation";
import {
  type AgentflowEditorCanvasScope,
  type AgentflowEditorDocument,
  type AgentflowEditorEdgeData,
  type AgentflowEditorHistoryMode,
  type AgentflowEditorNodeData,
  useAgentflowEditorStore,
} from "./agentflow-editor-store";

type DagNodeData = AgentflowEditorNodeData;

type DagEdgeData = AgentflowEditorEdgeData;

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

type CanvasScope = AgentflowEditorCanvasScope;

type NodeDataChangeHandler = (
  nodeId: string,
  update: Partial<DagNodeData>,
  historyMode?: AgentflowEditorHistoryMode,
) => void;

type EdgeDataChangeHandler = (
  edgeId: string,
  update: Partial<DagEdgeData>,
  historyMode?: AgentflowEditorHistoryMode,
) => void;

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
  [AgentflowNodeKind.ClearMessages]: {
    label: "Clear Messages",
    symbol: "Ø",
    tone: "border-orange-200 bg-orange-50 text-orange-900",
    body: "Discard upstream messages and continue with empty input",
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
  [AgentflowEdgeKind.FanInBarrier]: "Fan-in Barrier",
  [AgentflowEdgeKind.SwitchCase]: "If / Else If",
  [AgentflowEdgeKind.SwitchDefault]: "Else",
};

const EDGE_HELP_TEXT: Record<AgentflowEdgeKind, string> = {
  [AgentflowEdgeKind.Direct]:
    "MAF AddEdge: one source to one target, optionally guarded by a predicate.",
  [AgentflowEdgeKind.FanOut]:
    "MAF AddFanOutEdge: every target with a matching predicate runs; an empty predicate always matches.",
  [AgentflowEdgeKind.FanInBarrier]:
    "The target waits for every barrier source; in a controlled loop, the initial Input is reused on later iterations.",
  [AgentflowEdgeKind.SwitchCase]:
    "MAF AddSwitch: cases run in order and only the first matching target receives the message.",
  [AgentflowEdgeKind.SwitchDefault]:
    "MAF Switch default: this target runs only when no If or Else If predicate matches.",
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
  const nodes = useAgentflowEditorStore((state) => state.document.nodes);
  const edges = useAgentflowEditorStore((state) => state.document.edges);
  const agentflowName = useAgentflowEditorStore((state) => state.document.name);
  const agentflowDescription = useAgentflowEditorStore((state) => state.document.description);
  const summaryModelProviderId = useAgentflowEditorStore(
    (state) => state.document.summaryModelProviderId,
  );
  const selectedNodeId = useAgentflowEditorStore((state) => state.selectedNodeId);
  const selectedEdgeId = useAgentflowEditorStore((state) => state.selectedEdgeId);
  const canvasScope = useAgentflowEditorStore((state) => state.canvasScope);
  const pendingFocusNodeId = useAgentflowEditorStore((state) => state.pendingFocusNodeId);
  const isSaving = useAgentflowEditorStore((state) => state.isSaving);
  const updateDocument = useAgentflowEditorStore((state) => state.updateDocument);
  const commitHistoryGroup = useAgentflowEditorStore((state) => state.commitHistoryGroup);
  const markSaved = useAgentflowEditorStore((state) => state.markSaved);
  const setSaving = useAgentflowEditorStore((state) => state.setSaving);
  const selectNode = useAgentflowEditorStore((state) => state.selectNode);
  const selectEdge = useAgentflowEditorStore((state) => state.selectEdge);
  const clearSelection = useAgentflowEditorStore((state) => state.clearSelection);
  const setCanvasScope = useAgentflowEditorStore((state) => state.setCanvasScope);
  const setPendingFocusNodeId = useAgentflowEditorStore((state) => state.setPendingFocusNodeId);
  const [reactFlowCanvas, setReactFlowCanvas] = React.useState<{
    key: string;
    instance: ReactFlowInstance<DagNodeData, DagEdgeData>;
  } | null>(null);

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

      updateDocument((document) => ({
        ...document,
        nodes: document.nodes
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
        edges: document.edges.filter((edge) => edge.source !== nodeId && edge.target !== nodeId),
      }));
    },
    [updateDocument],
  );

  const handleNodesChange = React.useCallback(
    (changes: NodeChange[]) => {
      const applicableChanges = changes.filter(
        (change) => change.type !== "remove" && change.type !== "select",
      );
      if (applicableChanges.length === 0) return;

      const historyMode: AgentflowEditorHistoryMode = applicableChanges.some(
        (change) => change.type === "position",
      )
        ? { group: "node-position" }
        : "ephemeral";
      updateDocument(
        (document) => ({
          ...document,
          nodes: applyNodeChanges(applicableChanges, document.nodes),
        }),
        historyMode,
      );
    },
    [updateDocument],
  );

  const handleEdgesChange = React.useCallback(
    (changes: EdgeChange[]) => {
      if (canvasScope.kind === "block") return;
      const applicableChanges = changes.filter((change) => change.type !== "select");
      if (applicableChanges.length === 0) return;
      updateDocument((document) => ({
        ...document,
        edges: applyEdgeChanges(applicableChanges, document.edges),
      }));
    },
    [canvasScope, updateDocument],
  );

  const blockMembership = React.useMemo(() => createBlockMembership(nodes, edges), [edges, nodes]);
  const activeBlockNode = React.useMemo(() => {
    if (canvasScope.kind !== "block") return null;
    const node = nodes.find((item) => item.id === canvasScope.blockId) ?? null;
    return node && isBlockNodeKind(node.data.kind) ? node : null;
  }, [canvasScope, nodes]);
  const openBlockScope = React.useCallback(
    (blockId: string) => {
      setPendingFocusNodeId(null);
      setCanvasScope({ kind: "block", blockId });
      selectNode(blockId);
    },
    [selectNode, setCanvasScope, setPendingFocusNodeId],
  );
  const exitBlockScope = React.useCallback(() => {
    setPendingFocusNodeId(null);
    if (canvasScope.kind === "block") {
      selectNode(canvasScope.blockId);
    }
    setCanvasScope({ kind: "root" });
  }, [canvasScope, selectNode, setCanvasScope, setPendingFocusNodeId]);
  const selectBlockParticipant = React.useCallback(
    (blockId: string, participantNodeId: string) => {
      setCanvasScope({ kind: "block", blockId });
      selectNode(participantNodeId);
    },
    [selectNode, setCanvasScope],
  );
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

    selectNode(blockMembership.participantOwnersByNodeId.get(selectedNodeId)?.[0] ?? null);
  }, [blockMembership, canvasScope, selectNode, selectedNodeId]);

  React.useEffect(() => {
    if (canvasScope.kind === "root") return;
    if (activeBlockNode) return;

    setCanvasScope({ kind: "root" });
    clearSelection();
  }, [activeBlockNode, canvasScope, clearSelection, setCanvasScope]);

  const updateNodeData = React.useCallback<NodeDataChangeHandler>(
    (nodeId, update, historyMode = "atomic") => {
      updateDocument(
        (document) => ({
          ...document,
          nodes: document.nodes.map((node) =>
            node.id === nodeId ? { ...node, data: { ...node.data, ...update } } : node,
          ),
        }),
        historyMode,
      );
    },
    [updateDocument],
  );

  const updateEdgeData = React.useCallback<EdgeDataChangeHandler>(
    (edgeId, update, historyMode = "atomic") => {
      updateDocument(
        (document) => ({
          ...document,
          edges: normalizeSwitchCaseOrders(
            document.edges.map((edge) => {
              if (edge.id !== edgeId) return edge;

              const previousData = { ...createDefaultEdgeData(), ...edge.data };
              const nextKind = update.kind ?? previousData.kind;
              const nextData = { ...previousData, ...update };
              if (nextKind === AgentflowEdgeKind.SwitchCase) {
                nextData.configJson = setSwitchCaseOrder(
                  nextData.configJson,
                  previousData.kind === AgentflowEdgeKind.SwitchCase
                    ? (getSwitchCasePosition(document.edges, edgeId)?.index ??
                        getNextSwitchCaseOrder(document.edges, edge.source))
                    : getNextSwitchCaseOrder(document.edges, edge.source),
                );
              } else {
                nextData.configJson = setSwitchCaseOrder(nextData.configJson, null);
              }
              if (
                nextKind === AgentflowEdgeKind.SwitchDefault ||
                nextKind === AgentflowEdgeKind.FanInBarrier
              ) {
                nextData.conditionJson = "";
              }

              return applyEdgeVisuals({
                ...edge,
                data: nextData,
                label: nextData.label || undefined,
              });
            }),
          ),
        }),
        historyMode,
      );
    },
    [updateDocument],
  );

  const deleteFlowEdge = React.useCallback(
    (edgeId: string) => {
      updateDocument((document) => ({
        ...document,
        edges: removeAgentflowEdge(document.edges, edgeId).map(applyEdgeVisuals),
      }));
    },
    [updateDocument],
  );

  const moveSwitchCase = React.useCallback(
    (edgeId: string, direction: -1 | 1) => {
      updateDocument((document) => ({
        ...document,
        edges: moveSwitchCaseEdge(document.edges, edgeId, direction).map(applyEdgeVisuals),
      }));
    },
    [updateDocument],
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

      updateDocument((document) => ({
        ...document,
        nodes: [...document.nodes, node],
      }));
      selectNode(nodeId);
    },
    [nodes.length, selectNode, updateDocument],
  );

  const onConnect = React.useCallback(
    (params: Connection) => {
      if (canvasScope.kind === "block") return;
      if (!params.source || !params.target) return;
      if (params.target === INPUT_NODE_ID) {
        toast.error("Input cannot have incoming edges");
        return;
      }

      const edgeKind = getDefaultEdgeKindForSource(
        edges,
        params.source,
        params.source === INPUT_NODE_ID ? AgentflowEdgeKind.FanOut : AgentflowEdgeKind.Direct,
      );
      const edgeData = createDefaultEdgeData(edgeKind);
      if (edgeKind === AgentflowEdgeKind.SwitchCase) {
        edgeData.configJson = setSwitchCaseOrder(
          edgeData.configJson,
          getNextSwitchCaseOrder(edges, params.source),
        );
      }

      const edge: Edge<DagEdgeData> = {
        id: `edge-${params.source}-${params.target}-${Date.now()}`,
        source: params.source,
        target: params.target,
        sourceHandle: params.sourceHandle,
        targetHandle: params.targetHandle,
        data: edgeData,
      };

      updateDocument((document) => ({
        ...document,
        edges: addEdge(applyEdgeVisuals(edge), document.edges),
      }));
    },
    [canvasScope, edges, updateDocument],
  );

  const onSelectionChange = React.useCallback(
    (selection: OnSelectionChangeParams) => {
      const selectedNode = selection.nodes[0];
      const selectedEdge = selection.edges[0];

      if (selectedNode) {
        selectNode(selectedNode.id);
        return;
      }

      if (selectedEdge && canvasScope.kind === "root") {
        selectEdge(selectedEdge.id);
        return;
      }

      // ReactFlow can emit a transient empty selection while controlled node selection is syncing.
      // Pane clicks own the intentional clear/select-parent behavior below.
    },
    [canvasScope, selectEdge, selectNode],
  );

  const addBlockParticipant = React.useCallback(
    (blockId: string, kind: AgentflowNodeKind, title: string, relateId: string) => {
      const nodeId = `${kind}-participant-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;

      updateDocument((document) => {
        const blockNode = document.nodes.find((node) => node.id === blockId);
        if (!blockNode) return document;

        const participantNode: Node<DagNodeData> = {
          id: nodeId,
          type: "dagNode",
          position: getNextBlockParticipantPosition(document.nodes, blockId),
          data: {
            kind,
            title,
            relateId,
            instructions: "",
            configJson: "",
          },
        };

        return {
          ...document,
          nodes: [
            ...document.nodes.map((node) =>
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
          ],
        };
      });
      setCanvasScope({ kind: "block", blockId });
      setPendingFocusNodeId(nodeId);
      selectNode(nodeId);
    },
    [selectNode, setCanvasScope, setPendingFocusNodeId, updateDocument],
  );

  const removeBlockParticipant = React.useCallback(
    (blockId: string, participantNodeId: string) => {
      updateDocument((document) => ({
        ...document,
        nodes: document.nodes.map((node) =>
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
      }));
      selectNode(blockId);
    },
    [selectNode, updateDocument],
  );

  const deleteBlockParticipant = React.useCallback(
    (blockId: string, participantNodeId: string) => {
      if (!canDeleteBlockMember(blockMembership, participantNodeId)) {
        updateDocument((document) => ({
          ...document,
          nodes: document.nodes.map((node) =>
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
        }));
        setCanvasScope({ kind: "root" });
        selectNode(participantNodeId);
        toast.info("Member removed from this block and kept in the workflow.");
        return;
      }

      deleteFlowNode(participantNodeId);
      selectNode(blockId);
    },
    [blockMembership, deleteFlowNode, selectNode, setCanvasScope, updateDocument],
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
  const canvasEdges = React.useMemo(
    () =>
      canvasScope.kind === "block"
        ? blockCanvasEdges
        : rootVisibleEdges.map((edge) => ({
            ...edge,
            selected: edge.id === selectedEdgeId,
            label: edge.data?.label || getEdgeRoutingLabel(edge, edges) || edge.label || undefined,
          })),
    [blockCanvasEdges, canvasScope, edges, rootVisibleEdges, selectedEdgeId],
  );
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
    updateDocument((document) => ({
      ...document,
      nodes: document.nodes.map((node) => {
        const position = positionByNodeId.get(node.id);
        return position ? { ...node, position } : node;
      }),
    }));
  }, [canvasEdges, canvasNodes, updateDocument]);

  const graphValidation = React.useMemo(() => {
    const validation = validateAgentflowGraph(nodes, edges);
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

  const setAgentflowName = React.useCallback(
    (name: string) => {
      updateDocument((document) => ({ ...document, name }), { group: "agentflow-name" });
    },
    [updateDocument],
  );
  const setAgentflowDescription = React.useCallback(
    (description: string) => {
      updateDocument((document) => ({ ...document, description }), {
        group: "agentflow-description",
      });
    },
    [updateDocument],
  );
  const setSummaryModelProviderId = React.useCallback(
    (summaryModelProviderId: string) => {
      updateDocument((document) => ({ ...document, summaryModelProviderId }));
    },
    [updateDocument],
  );

  const handleBuild = React.useCallback(async () => {
    commitHistoryGroup();
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

    setSaving(true);
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

      markSaved();
      onAgentflowCreated?.();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Failed to save agentflow");
    } finally {
      setSaving(false);
    }
  }, [
    agentflowDescription,
    agentflowName,
    commitHistoryGroup,
    editingAgentflow,
    edges,
    graphValidation,
    markSaved,
    nodes,
    onAgentflowCreated,
    setSaving,
    summaryModelProviderId,
  ]);

  const actionState = React.useMemo<AgentflowBuilderActionState>(() => {
    let label = "";
    if (isSaving) {
      label = editingAgentflow ? "Updating..." : "Creating..."; // "Saving..."
    } else {
      label = editingAgentflow ? "Update" : "Create";
    }
    return {
      label: label,
      disabled: isSaving || !agentflowName.trim() || !graphValidation.ok,
      isSaving,
      submit: handleBuild,
    };
  }, [agentflowName, editingAgentflow, graphValidation.ok, handleBuild, isSaving]);

  React.useEffect(() => {
    onActionStateChange?.(actionState);
    return () => onActionStateChange?.(null);
  }, [actionState, onActionStateChange]);

  return (
    <div
      className="grid h-full min-h-0 grid-cols-[320px_minmax(0,1fr)_340px]"
      onBlurCapture={commitHistoryGroup}
    >
      <aside className="min-h-0 overflow-auto agw-scrollbar border-r bg-muted/20 p-3">
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

      <section className="relative min-h-0 overflow-hidden bg-background">
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
          onNodeDragStart={commitHistoryGroup}
          onNodeDragStop={commitHistoryGroup}
          onNodeClick={(_, node) => {
            selectNode(node.id);
          }}
          onNodeDoubleClick={(_, node) => {
            if (isBlockNodeKind(node.data.kind)) {
              openBlockScope(node.id);
            }
          }}
          onEdgeClick={(_, edge) => {
            if (canvasScope.kind === "block") return;
            selectEdge(edge.id);
          }}
          onPaneClick={() => {
            if (canvasScope.kind === "block") {
              selectNode(canvasScope.blockId);
              return;
            }

            clearSelection();
          }}
          nodesConnectable={canvasScope.kind === "root"}
          deleteKeyCode={null}
          fitView
        >
          <Background variant={BackgroundVariant.Dots} gap={18} size={1} />
          <FlowControls onAutoLayout={handleAutoLayout} />
        </ReactFlow>
      </section>

      <aside className="flex min-h-0 flex-col overflow-hidden border-l bg-muted/20">
        <div className="border-b p-3">
          <p className="text-sm font-medium">Inspector</p>
          <p className="text-xs text-muted-foreground">Edit selected node or edge.</p>
        </div>
        <div className="min-h-0 flex-1 overflow-auto agw-scrollbar p-3">
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
            <EdgeInspector
              edge={selectedEdge}
              edges={edges}
              onChange={updateEdgeData}
              onDelete={deleteFlowEdge}
              onMoveSwitchCase={moveSwitchCase}
            />
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
            label="Clear Messages"
            onClick={() => onAddNode(AgentflowNodeKind.ClearMessages, "Clear Messages")}
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
  onChange: NodeDataChangeHandler;
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
  const usesAdvancedConfig = node.data.kind !== AgentflowNodeKind.ClearMessages;
  const configIsInvalid =
    usesAdvancedConfig &&
    node.data.configJson.trim().length > 0 &&
    readConfigJson(node.data.configJson) === null;
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
    node.data.kind !== AgentflowNodeKind.CheckpointMarker &&
    node.data.kind !== AgentflowNodeKind.ClearMessages;

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
          onChange={(event) =>
            onChange(node.id, { title: event.target.value }, { group: `node:${node.id}:title` })
          }
        />
      </div>

      {usesInstructions ? (
        <div className="space-y-2">
          <Label>System Prompt / Instructions</Label>
          <Textarea
            value={node.data.instructions}
            onChange={(event) =>
              onChange(
                node.id,
                { instructions: event.target.value },
                { group: `node:${node.id}:instructions` },
              )
            }
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

      {usesAdvancedConfig ? (
        <div className="space-y-2">
          <Label>Advanced Config JSON</Label>
          <Textarea
            value={node.data.configJson}
            onChange={(event) =>
              onChange(
                node.id,
                { configJson: event.target.value },
                { group: `node:${node.id}:config` },
              )
            }
            placeholder='{ "key": "value" }'
            className="min-h-24 font-mono text-xs"
          />
        </div>
      ) : null}
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
  onChange: NodeDataChangeHandler;
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
  onChange: NodeDataChangeHandler;
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

  const setConfig = (
    update: Record<string, unknown>,
    historyMode: AgentflowEditorHistoryMode = "atomic",
  ) => {
    onChange(node.id, { configJson: updateConfigJson(node.data.configJson, update) }, historyMode);
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
          onChange={(value) =>
            setConfig({ maxRounds: value }, { group: `node:${node.id}:max-rounds` })
          }
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
              onChange={(value) =>
                setConfig({ maxStalls: value }, { group: `node:${node.id}:max-stalls` })
              }
            />
            <ConfigNumberField
              label="Max Resets"
              value={readNumber(config.maxResets)}
              onChange={(value) =>
                setConfig({ maxResets: value }, { group: `node:${node.id}:max-resets` })
              }
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
              onChange={(event) =>
                setConfig(
                  { handoffInstructions: event.target.value },
                  { group: `node:${node.id}:handoff-instructions` },
                )
              }
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
                onChange={(value) =>
                  setConfig(
                    { autonomousTurnLimit: value },
                    { group: `node:${node.id}:autonomous-turn-limit` },
                  )
                }
              />
              <div className="space-y-2">
                <Label>Continuation Prompt</Label>
                <Textarea
                  value={readString(config.continuationPrompt)}
                  onChange={(event) =>
                    setConfig(
                      { continuationPrompt: event.target.value },
                      { group: `node:${node.id}:continuation-prompt` },
                    )
                  }
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
  onChange: NodeDataChangeHandler;
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
  onChange: NodeDataChangeHandler;
}) {
  const config = readConfigJson(node.data.configJson) ?? {};
  const setConfig = (
    update: Record<string, unknown>,
    historyMode: AgentflowEditorHistoryMode = "atomic",
  ) => {
    onChange(node.id, { configJson: updateConfigJson(node.data.configJson, update) }, historyMode);
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
          onChange={(event) =>
            setConfig(
              { humanPrompt: event.target.value },
              { group: `node:${node.id}:human-prompt` },
            )
          }
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
  onChange: NodeDataChangeHandler;
}) {
  const config = readConfigJson(node.data.configJson) ?? {};
  const setConfig = (
    update: Record<string, unknown>,
    historyMode: AgentflowEditorHistoryMode = "atomic",
  ) => {
    onChange(node.id, { configJson: updateConfigJson(node.data.configJson, update) }, historyMode);
  };

  return (
    <div className="space-y-2 rounded-md border bg-background p-3">
      <Label>Checkpoint Name</Label>
      <Input
        value={readString(config.checkpointName)}
        onChange={(event) =>
          setConfig(
            { checkpointName: event.target.value },
            { group: `node:${node.id}:checkpoint-name` },
          )
        }
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
  edges,
  onChange,
  onDelete,
  onMoveSwitchCase,
}: {
  edge: Edge<DagEdgeData>;
  edges: Edge<DagEdgeData>[];
  onChange: EdgeDataChangeHandler;
  onDelete: (edgeId: string) => void;
  onMoveSwitchCase: (edgeId: string, direction: -1 | 1) => void;
}) {
  const data = { ...createDefaultEdgeData(), ...edge.data };
  const switchPosition = getSwitchCasePosition(edges, edge.id);
  const routingLabel = getEdgeRoutingLabel(edge, edges);
  const hasOtherDefault = edges.some(
    (candidate) =>
      candidate.id !== edge.id &&
      candidate.source === edge.source &&
      candidate.data?.kind === AgentflowEdgeKind.SwitchDefault,
  );

  return (
    <div className="space-y-3">
      <div className="rounded-md border bg-background p-3 shadow-sm">
        <div className="flex items-start justify-between gap-3">
          <div className="min-w-0">
            <div className="flex items-center gap-2">
              <p className="text-sm font-medium">{EDGE_LABELS[data.kind]}</p>
              {routingLabel ? (
                <Badge variant="outline" className="font-mono text-[10px] tracking-wider">
                  {routingLabel}
                </Badge>
              ) : null}
            </div>
            <p className="mt-1 truncate font-mono text-[11px] text-muted-foreground">
              {edge.source} {"->"} {edge.target}
            </p>
          </div>
          <Button
            type="button"
            variant="outline"
            size="icon"
            title="Delete edge"
            aria-label="Delete edge"
            className="shrink-0 text-destructive hover:border-destructive/50 hover:bg-destructive/10 hover:text-destructive"
            onClick={() => onDelete(edge.id)}
          >
            <Trash2 className="h-4 w-4" />
          </Button>
        </div>
        <p className="mt-2 text-xs text-muted-foreground">{EDGE_HELP_TEXT[data.kind]}</p>
      </div>
      <div className="space-y-2">
        <Label>Edge Type</Label>
        <Select
          value={String(data.kind)}
          onValueChange={(value) => {
            const kind = Number(value) as AgentflowEdgeKind;
            onChange(edge.id, { kind });
          }}
        >
          <SelectTrigger>
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {Object.entries(EDGE_LABELS).map(([value, label]) => (
              <SelectItem
                key={value}
                value={value}
                disabled={Number(value) === AgentflowEdgeKind.SwitchDefault && hasOtherDefault}
              >
                {label}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </div>
      {data.kind === AgentflowEdgeKind.SwitchCase && switchPosition ? (
        <div className="rounded-md border border-violet-200 bg-violet-50/70 p-3 dark:border-violet-900 dark:bg-violet-950/30">
          <div className="flex items-center justify-between gap-3">
            <div>
              <p className="text-xs font-medium text-violet-950 dark:text-violet-100">
                Branch {switchPosition.index + 1} of {switchPosition.count}
              </p>
              <p className="mt-1 text-[11px] text-violet-700 dark:text-violet-300">
                Cases are evaluated from top to bottom.
              </p>
            </div>
            <div className="flex gap-1">
              <Button
                type="button"
                variant="outline"
                size="icon"
                title="Move branch up"
                aria-label="Move branch up"
                disabled={switchPosition.index === 0}
                onClick={() => onMoveSwitchCase(edge.id, -1)}
              >
                <ArrowUp className="h-4 w-4" />
              </Button>
              <Button
                type="button"
                variant="outline"
                size="icon"
                title="Move branch down"
                aria-label="Move branch down"
                disabled={switchPosition.index === switchPosition.count - 1}
                onClick={() => onMoveSwitchCase(edge.id, 1)}
              >
                <ArrowDown className="h-4 w-4" />
              </Button>
            </div>
          </div>
        </div>
      ) : null}
      <div className="space-y-2">
        <Label>Label</Label>
        <Input
          value={data.label}
          onChange={(event) =>
            onChange(edge.id, { label: event.target.value }, { group: `edge:${edge.id}:label` })
          }
        />
      </div>
      {isPredicateEdgeKind(data.kind) ? (
        <div className="space-y-2">
          <Label>Predicate JSON</Label>
          <Textarea
            value={data.conditionJson}
            onChange={(event) =>
              onChange(
                edge.id,
                { conditionJson: event.target.value },
                { group: `edge:${edge.id}:condition` },
              )
            }
            placeholder='{ "contains": "approved", "minMessages": 1 }'
            className="min-h-28 font-mono text-xs"
          />
          <p className="text-xs text-muted-foreground">
            {data.kind === AgentflowEdgeKind.SwitchCase
              ? "Required. The first matching branch wins."
              : data.kind === AgentflowEdgeKind.FanOut
                ? "Optional. Every matching target runs; blank means always selected."
                : "Optional. Direct predicates are evaluated independently."}
            {" Keys: always, contains, notContains, equals, author, role, minMessages."}
          </p>
        </div>
      ) : (
        <div className="rounded-md border bg-background p-3 text-xs text-muted-foreground">
          {data.kind === AgentflowEdgeKind.SwitchDefault
            ? "Else does not use a predicate and always remains after all If branches."
            : "Barrier edges are structural and do not use predicate JSON."}
        </div>
      )}
      {data.kind === AgentflowEdgeKind.SwitchCase ? (
        <div className="rounded-md border border-dashed p-3 text-xs text-muted-foreground">
          Branch order is maintained by the controls above. Other existing advanced config values
          are preserved when the branch moves.
        </div>
      ) : (
        <div className="space-y-2">
          <Label>Advanced Config JSON</Label>
          <Textarea
            value={data.configJson}
            onChange={(event) =>
              onChange(
                edge.id,
                { configJson: event.target.value },
                { group: `edge:${edge.id}:config` },
              )
            }
            className="min-h-20 font-mono text-xs"
          />
        </div>
      )}
    </div>
  );
}

export function createAgentflowEditorDocument({
  editingAgentflow,
  agents,
  agentflows,
}: {
  editingAgentflow?: AgentflowDetailDto | null;
  agents: AgentDto[];
  agentflows: AgentflowDto[];
}): AgentflowEditorDocument {
  if (!editingAgentflow) {
    return {
      name: "",
      description: "",
      summaryModelProviderId: "",
      nodes: [createInputNode<DagNodeData>()],
      edges: [],
    };
  }

  const loadedNodes = editingAgentflow.nodes.map((node, index) => ({
    id: node.nodeId,
    type: "dagNode",
    position: parsePosition(node.positionJson, index),
    data: {
      kind: node.kind,
      title: node.name || resolveNodeTitle(node, agents, agentflows),
      relateId: node.relateId,
      instructions: node.instructions || "",
      configJson: node.configJson || "",
    },
  })) satisfies Node<DagNodeData>[];
  const loadedEdges = editingAgentflow.edges.map((edge) => createFlowEdge(edge));
  const normalizedGraph = ensureInputGraph(loadedNodes, loadedEdges);

  return {
    name: editingAgentflow.name,
    description: editingAgentflow.description || "",
    summaryModelProviderId: editingAgentflow.summaryModelProviderId ?? "",
    nodes: normalizedGraph.nodes,
    edges: normalizeSwitchCaseOrders(normalizedGraph.edges).map(applyEdgeVisuals),
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

  if (kind === AgentflowEdgeKind.FanInBarrier) {
    return { color: "#d97706", width: 2, animated: false };
  }

  if (kind === AgentflowEdgeKind.SwitchCase) {
    return { color: "#7c3aed", width: 2, animated: false };
  }

  if (kind === AgentflowEdgeKind.SwitchDefault) {
    return { color: "#db2777", width: 2, animated: false };
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

function validateAgentflowGraph(nodes: Node<DagNodeData>[], edges: Edge<DagEdgeData>[]) {
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

  const routingError = validateAgentflowEdgeRouting(edges);
  if (routingError) {
    return { ok: false, message: routingError };
  }

  const cycleError = validateAgentflowCycles(nodes, edges);
  if (cycleError) {
    return { ok: false, message: cycleError };
  }

  return {
    ok: true,
    message: `Valid workflow graph · ${nodes.length} nodes · ${edges.length} edges`,
  };
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
