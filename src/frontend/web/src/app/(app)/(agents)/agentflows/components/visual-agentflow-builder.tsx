"use client";

import * as React from "react";
import ReactFlow, {
  Background,
  BackgroundVariant,
  Connection,
  ControlButton,
  Controls,
  Edge,
  Handle,
  MarkerType,
  Node,
  NodeProps,
  NodeTypes,
  OnSelectionChangeParams,
  Position,
  addEdge,
  useEdgesState,
  useNodesState,
  useReactFlow,
} from "reactflow";
import "reactflow/dist/style.css";

import { apiPost, apiPut } from "@/api/client";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  Select,
  SelectContent,
  SelectGroup,
  SelectItem,
  SelectLabel,
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
} from "@/types/agentflow";
import { Bot, Grid, Maximize2, Workflow } from "lucide-react";
import { toast } from "sonner";
import { createGraphLayout } from "./autoLayout";

type DagNodeData = {
  kind: AgentflowNodeKind;
  title: string;
  relateId: string | null;
  instructions: string;
  configJson: string;
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
  editingAgentflow?: AgentflowDetailDto | null;
  onAgentflowCreated?: () => void;
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
};

const EDGE_LABELS: Record<AgentflowEdgeKind, string> = {
  [AgentflowEdgeKind.Direct]: "Direct",
  [AgentflowEdgeKind.FanOut]: "Fan Out",
  [AgentflowEdgeKind.FanIn]: "Fan In",
};

const EDGE_HELP_TEXT: Record<AgentflowEdgeKind, string> = {
  [AgentflowEdgeKind.Direct]: "MAF AddEdge: one source to one target, optionally guarded by a predicate.",
  [AgentflowEdgeKind.FanOut]: "MAF AddFanOutEdge: one source broadcasts the same input to multiple targets.",
  [AgentflowEdgeKind.FanIn]: "MAF AddFanInBarrierEdge: multiple sources join before the target runs.",
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

function DagNode({ data, selected }: NodeProps<DagNodeData>) {
  const meta = NODE_META[data.kind];

  return (
    <Card
      className={`w-[220px] gap-0 overflow-hidden rounded-md border-2 p-0 shadow-sm transition-shadow ${
        selected ? "border-primary shadow-md" : "border-border"
      }`}
    >
      <Handle
        type="target"
        position={Position.Left}
        className="h-3 w-3 border-2 border-background !bg-sky-600"
      />
      <CardHeader className={`px-3 py-2 ${meta.tone}`}>
        <div className="flex min-w-0 items-center gap-2">
          <div className="grid h-7 w-7 shrink-0 place-items-center rounded border bg-background/80 text-xs font-semibold">
            {meta.symbol}
          </div>
          <div className="min-w-0">
            <CardTitle className="truncate text-sm">{data.title}</CardTitle>
            <div className="mt-0.5 text-[10px] uppercase tracking-wide opacity-70">{meta.label}</div>
          </div>
        </div>
      </CardHeader>
      <CardContent className="px-3 py-2 text-xs text-muted-foreground">{meta.body}</CardContent>
      <Handle
        type="source"
        position={Position.Right}
        className="h-3 w-3 border-2 border-background !bg-emerald-600"
      />
    </Card>
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
  editingAgentflow,
  onAgentflowCreated,
}: VisualAgentflowBuilderProps) {
  const [nodes, setNodes, onNodesChange] = useNodesState<DagNodeData>([]);
  const [edges, setEdges, onEdgesChange] = useEdgesState<DagEdgeData>([]);
  const [selectedNodeId, setSelectedNodeId] = React.useState<string | null>(null);
  const [selectedEdgeId, setSelectedEdgeId] = React.useState<string | null>(null);
  const [selectedAgentId, setSelectedAgentId] = React.useState<string>("");
  const [selectedAgentflowId, setSelectedAgentflowId] = React.useState<string>("");
  const [agentflowName, setAgentflowName] = React.useState("");
  const [agentflowDescription, setAgentflowDescription] = React.useState("");
  const [agentflowEnabled, setAgentflowEnabled] = React.useState(true);
  const [isSaving, setIsSaving] = React.useState(false);

  const availableAgentflows = React.useMemo(() => {
    if (!editingAgentflow) return agentflows;
    return agentflows.filter((agentflow) => agentflow.id !== editingAgentflow.id);
  }, [agentflows, editingAgentflow]);

  const selectedNode = React.useMemo(
    () => nodes.find((node) => node.id === selectedNodeId) ?? null,
    [nodes, selectedNodeId],
  );
  const selectedEdge = React.useMemo(
    () => edges.find((edge) => edge.id === selectedEdgeId) ?? null,
    [edges, selectedEdgeId],
  );

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

          const nextData = { ...createDefaultEdgeData(), ...edge.data, ...update };
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
      setAgentflowEnabled(true);
      setNodes([]);
      setEdges([]);
      setSelectedNodeId(null);
      setSelectedEdgeId(null);
      return;
    }

    setAgentflowName(editingAgentflow.name);
    setAgentflowDescription(editingAgentflow.description || "");
    setAgentflowEnabled(editingAgentflow.enable);

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
    setNodes(loadedNodes);
    setEdges(loadedEdges);
    setSelectedNodeId(loadedNodes[0]?.id ?? null);
    setSelectedEdgeId(null);
  }, [agents, agentflows, editingAgentflow, setEdges, setNodes]);

  const onConnect = React.useCallback(
    (params: Connection) => {
      if (!params.source || !params.target) return;

      const edge: Edge<DagEdgeData> = {
        id: `edge-${params.source}-${params.target}-${Date.now()}`,
        source: params.source,
        target: params.target,
        sourceHandle: params.sourceHandle,
        targetHandle: params.targetHandle,
        data: createDefaultEdgeData(),
      };

      setEdges((current) =>
        addEdge(
          applyEdgeVisuals(edge),
          current,
        ),
      );
    },
    [setEdges],
  );

  const onSelectionChange = React.useCallback((selection: OnSelectionChangeParams) => {
    const selectedNode = selection.nodes[0];
    const selectedEdge = selection.edges[0];
    setSelectedNodeId(selectedNode?.id ?? null);
    setSelectedEdgeId(selectedNode ? null : selectedEdge?.id ?? null);
  }, []);

  const handleAutoLayout = React.useCallback(async () => {
    if (nodes.length === 0) return;
    const result = await createGraphLayout(nodes, edges);
    setNodes(result.nodes as Node<DagNodeData>[]);
    setEdges(result.edges as Edge<DagEdgeData>[]);
  }, [edges, nodes, setEdges, setNodes]);

  const handleDeleteSelection = React.useCallback(() => {
    if (selectedNodeId) {
      setNodes((current) => current.filter((node) => node.id !== selectedNodeId));
      setEdges((current) =>
        current.filter((edge) => edge.source !== selectedNodeId && edge.target !== selectedNodeId),
      );
      setSelectedNodeId(null);
      return;
    }

    if (selectedEdgeId) {
      setEdges((current) => current.filter((edge) => edge.id !== selectedEdgeId));
      setSelectedEdgeId(null);
    }
  }, [selectedEdgeId, selectedNodeId, setEdges, setNodes]);

  const graphValidation = React.useMemo(() => validateDag(nodes, edges), [nodes, edges]);

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
      enable: agentflowEnabled,
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
      setAgentflowEnabled(true);
      setNodes([]);
      setEdges([]);
      setSelectedNodeId(null);
      setSelectedEdgeId(null);
      onAgentflowCreated?.();
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Failed to save agentflow");
    } finally {
      setIsSaving(false);
    }
  }, [
    agentflowDescription,
    agentflowEnabled,
    agentflowName,
    editingAgentflow,
    edges,
    graphValidation,
    nodes,
    onAgentflowCreated,
    setEdges,
    setNodes,
  ]);

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

          <div className="flex items-center justify-between rounded-md border bg-background px-3 py-2">
            <Label htmlFor="agentflowEnabled" className="cursor-pointer">
              Enabled
            </Label>
            <Switch id="agentflowEnabled" checked={agentflowEnabled} onCheckedChange={setAgentflowEnabled} />
          </div>
        </div>

        <div className="mt-5 space-y-3">
          <div>
            <p className="mb-2 text-xs font-medium uppercase tracking-wide text-muted-foreground">
              Primitive Nodes
            </p>
            <div className="space-y-2">
              <Select value={selectedAgentId} onValueChange={setSelectedAgentId}>
                <SelectTrigger>
                  <SelectValue placeholder="Select agent" />
                </SelectTrigger>
                <SelectContent>
                  <SelectGroup>
                    <SelectLabel>Agents</SelectLabel>
                    {agents.map((agent) => (
                      <SelectItem key={agent.id} value={agent.id}>
                        {agent.name}
                      </SelectItem>
                    ))}
                  </SelectGroup>
                </SelectContent>
              </Select>
              <Button
                type="button"
                variant="outline"
                className="w-full justify-start"
                disabled={!selectedAgentId}
                onClick={() => {
                  const agent = agents.find((item) => item.id === selectedAgentId);
                  if (agent) addDagNode(AgentflowNodeKind.Agent, agent.name, agent.id);
                }}
              >
                <Bot className="h-4 w-4" />
                Add Agent
              </Button>
              <Select value={selectedAgentflowId} onValueChange={setSelectedAgentflowId}>
                <SelectTrigger>
                  <SelectValue placeholder="Select workflow" />
                </SelectTrigger>
                <SelectContent>
                  <SelectGroup>
                    <SelectLabel>Agentflows</SelectLabel>
                    {availableAgentflows.map((agentflow) => (
                      <SelectItem key={agentflow.id} value={agentflow.id}>
                        {agentflow.name}
                      </SelectItem>
                    ))}
                  </SelectGroup>
                </SelectContent>
              </Select>
              <Button
                type="button"
                variant="outline"
                className="w-full justify-start"
                disabled={!selectedAgentflowId}
                onClick={() => {
                  const agentflow = availableAgentflows.find((item) => item.id === selectedAgentflowId);
                  if (agentflow) {
                    addDagNode(AgentflowNodeKind.WorkflowAsAgent, agentflow.name, agentflow.id);
                  }
                }}
              >
                <Workflow className="h-4 w-4" />
                Add Workflow As Agent
              </Button>
              <PaletteButton
                label="Prompt Adapter"
                onClick={() => addDagNode(AgentflowNodeKind.PromptAdapter, "Prompt Adapter")}
              />
              <PaletteButton
                label="Human Gate"
                onClick={() => addDagNode(AgentflowNodeKind.HumanGate, "Human Gate")}
              />
              <PaletteButton
                label="Checkpoint"
                onClick={() => addDagNode(AgentflowNodeKind.CheckpointMarker, "Checkpoint")}
              />
              <PaletteButton label="Output" onClick={() => addDagNode(AgentflowNodeKind.Output, "Output")} />
            </div>
          </div>

          <div>
            <p className="mb-2 text-xs font-medium uppercase tracking-wide text-muted-foreground">
              Orchestration Blocks
            </p>
            <div className="space-y-2">
              <PaletteButton
                label="Concurrent Block"
                onClick={() => addDagNode(AgentflowNodeKind.ConcurrentBlock, "Concurrent Block")}
              />
              <PaletteButton
                label="Handoff Group"
                onClick={() => addDagNode(AgentflowNodeKind.HandoffBlock, "Handoff Group")}
              />
              <PaletteButton
                label="GroupChat Room"
                onClick={() => addDagNode(AgentflowNodeKind.GroupChatBlock, "GroupChat Room")}
              />
              <PaletteButton
                label="Magentic Team"
                onClick={() => addDagNode(AgentflowNodeKind.MagenticBlock, "Magentic Team")}
              />
            </div>
          </div>
        </div>
      </aside>

      <section className="relative min-h-0 overflow-hidden rounded-md border bg-background">
        <div className="absolute left-3 top-3 z-10 flex items-center gap-2">
          <div
            className={`rounded-full border px-3 py-1 text-xs ${
              graphValidation.ok
                ? "border-emerald-200 bg-emerald-50 text-emerald-700"
                : "border-destructive/30 bg-destructive/10 text-destructive"
            }`}
          >
            {graphValidation.message}
          </div>
        </div>
        <ReactFlow
          nodes={nodes}
          edges={edges}
          nodeTypes={nodeTypes}
          onNodesChange={onNodesChange}
          onEdgesChange={onEdgesChange}
          onConnect={onConnect}
          onSelectionChange={onSelectionChange}
          onNodeClick={(_, node) => {
            setSelectedNodeId(node.id);
            setSelectedEdgeId(null);
          }}
          onEdgeClick={(_, edge) => {
            setSelectedEdgeId(edge.id);
            setSelectedNodeId(null);
          }}
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
          {selectedNode ? (
            <NodeInspector node={selectedNode} nodes={nodes} onChange={updateNodeData} />
          ) : selectedEdge ? (
            <EdgeInspector edge={selectedEdge} onChange={updateEdgeData} />
          ) : (
            <div className="rounded-md border border-dashed p-4 text-sm text-muted-foreground">
              Select a node or edge on the canvas.
            </div>
          )}
        </div>
        <div className="flex gap-2 border-t p-3">
          <Button
            type="button"
            variant="outline"
            className="flex-1"
            disabled={!selectedNodeId && !selectedEdgeId}
            onClick={handleDeleteSelection}
          >
            Delete
          </Button>
          <Button
            type="button"
            className="flex-1"
            disabled={isSaving || !agentflowName.trim() || nodes.length === 0}
            onClick={handleBuild}
          >
            {isSaving ? "Saving..." : editingAgentflow ? "Update" : "Create"}
          </Button>
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

function NodeInspector({
  node,
  nodes,
  onChange,
}: {
  node: Node<DagNodeData>;
  nodes: Node<DagNodeData>[];
  onChange: (nodeId: string, update: Partial<DagNodeData>) => void;
}) {
  const meta = NODE_META[node.data.kind];
  const configIsInvalid = node.data.configJson.trim().length > 0 && readConfigJson(node.data.configJson) === null;
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
        <BlockConfigInspector node={node} nodes={nodes} onChange={onChange} />
      ) : null}

      {node.data.kind === AgentflowNodeKind.HumanGate ? (
        <HumanGateConfigInspector node={node} onChange={onChange} />
      ) : null}

      {node.data.kind === AgentflowNodeKind.CheckpointMarker ? (
        <CheckpointConfigInspector node={node} onChange={onChange} />
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

function BlockConfigInspector({
  node,
  nodes,
  onChange,
}: {
  node: Node<DagNodeData>;
  nodes: Node<DagNodeData>[];
  onChange: (nodeId: string, update: Partial<DagNodeData>) => void;
}) {
  const config = readConfigJson(node.data.configJson) ?? {};
  const participantNodes = nodes.filter(
    (item) => item.id !== node.id && isAgentParticipantKind(item.data.kind),
  );
  const participantNodeIds = readStringArray(config.participantNodeIds);
  const selectedParticipants = participantNodes.filter((item) => participantNodeIds.includes(item.id));
  const managerNodeId = readString(config.managerNodeId);

  const setConfig = (update: Record<string, unknown>) => {
    onChange(node.id, { configJson: updateConfigJson(node.data.configJson, update) });
  };

  const toggleParticipant = (participantNodeId: string, checked: boolean) => {
    const nextIds = checked
      ? [...participantNodeIds, participantNodeId]
      : participantNodeIds.filter((id) => id !== participantNodeId);
    const nextUpdate: Record<string, unknown> = {
      participantNodeIds: Array.from(new Set(nextIds)),
    };

    if (managerNodeId && !nextIds.includes(managerNodeId)) {
      nextUpdate.managerNodeId = undefined;
    }

    setConfig(nextUpdate);
  };

  return (
    <div className="space-y-3 rounded-md border bg-background p-3">
      <div className="flex items-center justify-between gap-2">
        <Label>Participants</Label>
        <Badge variant="outline">{selectedParticipants.length} selected</Badge>
      </div>

      {participantNodes.length === 0 ? (
        <p className="text-xs text-muted-foreground">
          Add Agent or Workflow-as-Agent nodes, then select them as block participants.
        </p>
      ) : (
        <div className="space-y-2">
          {participantNodes.map((participant) => (
            <label
              key={participant.id}
              className="flex cursor-pointer items-start gap-2 rounded-md border px-2 py-2 text-sm"
            >
              <Checkbox
                checked={participantNodeIds.includes(participant.id)}
                onCheckedChange={(checked) => toggleParticipant(participant.id, checked === true)}
              />
              <span className="min-w-0">
                <span className="block truncate font-medium">{participant.data.title}</span>
                <span className="block text-xs text-muted-foreground">
                  {NODE_META[participant.data.kind].label}
                </span>
              </span>
            </label>
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
              value={participantNodeIds.includes(managerNodeId) ? managerNodeId : ""}
              onValueChange={(value) => setConfig({ managerNodeId: value })}
              disabled={participantNodeIds.length === 0}
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
          onValueChange={(value) => {
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
        <Input value={data.label} onChange={(event) => onChange(edge.id, { label: event.target.value })} />
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

function createDefaultEdgeData(): DagEdgeData {
  return {
    kind: AgentflowEdgeKind.Direct,
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

function getEdgeVisual(kind: AgentflowEdgeKind): { color: string; width: number; animated: boolean } {
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
    return agentflows.find((agentflow) => agentflow.id === node.relateId)?.name || "Unknown Workflow";
  }

  return NODE_META[node.kind]?.label || "Node";
}

function validateDag(nodes: Node<DagNodeData>[], edges: Edge<DagEdgeData>[]) {
  if (nodes.length === 0) {
    return { ok: false, message: "Add at least one node" };
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
      (node.data.kind === AgentflowNodeKind.Agent || node.data.kind === AgentflowNodeKind.WorkflowAsAgent) &&
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
        return { ok: false, message: `${node.data.title || node.id} needs at least two participants` };
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
        return { ok: false, message: `${node.data.title || node.id} manager must be a participant` };
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
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string") : [];
}

function parseOptionalInteger(value: string) {
  if (!value.trim()) return undefined;

  const parsed = Number(value);
  if (!Number.isFinite(parsed)) return undefined;
  return Math.max(1, Math.trunc(parsed));
}

function isAgentParticipantKind(kind: AgentflowNodeKind) {
  return kind === AgentflowNodeKind.Agent || kind === AgentflowNodeKind.WorkflowAsAgent;
}

function isBlockNodeKind(kind: AgentflowNodeKind) {
  return (
    kind === AgentflowNodeKind.ConcurrentBlock ||
    kind === AgentflowNodeKind.HandoffBlock ||
    kind === AgentflowNodeKind.GroupChatBlock ||
    kind === AgentflowNodeKind.MagenticBlock
  );
}
