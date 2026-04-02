"use client";

import * as React from "react";
import ReactFlow, {
  Node,
  Edge,
  Controls,
  ControlButton,
  Background,
  useNodesState,
  useEdgesState,
  addEdge,
  Connection,
  MarkerType,
  NodeTypes,
  BackgroundVariant,
  Handle,
  Position,
  useReactFlow,
} from "reactflow";
import "reactflow/dist/style.css";
import { Button } from "@/components/ui/button";
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
import { Input } from "@/components/ui/input";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Info, Workflow, Bot, Grid, Maximize2 } from "lucide-react";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Switch } from "@/components/ui/switch";
import {
  AgentDto,
  AgentflowDetailDto,
  AgentflowDto,
  AgentflowEdgeDto,
  AgentflowNodeDto,
  AgentflowNodeType,
} from "@/types/agentflow";
import { toast } from "sonner";
import { apiPost, apiPut } from "@/api/client";
import { createGraphLayout } from "./autoLayout";
import { GroupChatConfig, MagenticConfig } from "./pattern-configs";

type AgentNodeData = {
  nodeId: string;
  agentId: string;
  agentName: string;
  role: string;
  onDelete: (id: string) => void;
  onRoleChange: (id: string, role: string) => void;
};

type AgentflowNodeData = {
  nodeId: string;
  agentflowId: string;
  agentflowName: string;
  role: string;
  onDelete: (id: string) => void;
  onRoleChange: (id: string, role: string) => void;
};

// Custom Agentflow Node Component
function AgentflowNode({ data }: { data: AgentflowNodeData }) {
  return (
    <Card className="min-w-50 shadow-lg p-0 gap-2 border-purple-200 bg-purple-50">
      {/* Input Handle (Target) */}
      <Handle
        type="target"
        position={Position.Left}
        id="input"
        className="w-3 h-3 !bg-blue-500 border-2 border-white"
      />

      <CardHeader className="p-3 pb-2">
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-2">
            <Workflow className="w-4 h-4 text-purple-600" />
            <CardTitle className="text-sm font-semibold text-purple-700">
              {data.agentflowName}
            </CardTitle>
          </div>
          <button
            onClick={() => data.onDelete(data.nodeId)}
            className="text-xs text-muted-foreground hover:text-destructive cursor-pointer"
            title="Remove agentflow"
          >
            ✕
          </button>
        </div>
      </CardHeader>
      <CardContent className="p-3 pt-1">
        <Input
          value={data.role}
          onChange={(e) => data.onRoleChange(data.nodeId, e.target.value)}
          placeholder="Role (optional)"
          className="h-7 text-xs"
        />
      </CardContent>

      {/* Output Handle (Source) */}
      <Handle
        type="source"
        position={Position.Right}
        id="output"
        className="w-3 h-3 !bg-green-500 border-2 border-white"
      />
    </Card>
  );
}

// Custom Agent Node Component
function AgentNode({ data }: { data: AgentNodeData }) {
  return (
    <Card className="min-w-50 shadow-lg p-0 gap-2">
      {/* Input Handle (Target) */}
      <Handle
        type="target"
        position={Position.Left}
        id="input"
        className="w-3 h-3 !bg-blue-500 border-2 border-white"
      />

      <CardHeader className="p-3 pb-2">
        <div className="flex items-center justify-between">
          <Bot className="w-4 h-4 text-gray-900" />
          <CardTitle className="text-sm font-semibold">{data.agentName}</CardTitle>
          <button
            onClick={() => data.onDelete(data.nodeId)}
            className="text-xs text-muted-foreground hover:text-destructive cursor-pointer"
            title="Remove agent"
          >
            ✕
          </button>
        </div>
      </CardHeader>
      <CardContent className="p-3 pt-1">
        <Input
          value={data.role}
          onChange={(e) => data.onRoleChange(data.nodeId, e.target.value)}
          placeholder="Role (optional)"
          className="h-7 text-xs"
        />
      </CardContent>

      {/* Output Handle (Source) */}
      <Handle
        type="source"
        position={Position.Right}
        id="output"
        className="w-3 h-3 !bg-green-500 border-2 border-white"
      />
    </Card>
  );
}

const nodeTypes: NodeTypes = {
  agentNode: AgentNode,
  agentflowNode: AgentflowNode,
};

// Custom Controls component that uses useReactFlow hook
function FlowControls({
  pattern,
  onAutoLayout,
}: {
  pattern: number;
  onAutoLayout: (pattern: number) => void;
}) {
  const { fitView } = useReactFlow();

  return (
    <Controls>
      <ControlButton
        onClick={() => fitView({ padding: 0.2, duration: 200 })}
        title="Fit View - Center and zoom to fit all nodes"
      >
        <Maximize2 size={16} />
      </ControlButton>
      <ControlButton
        onClick={() => onAutoLayout(pattern)}
        title="Auto Layout - Arrange nodes based on current pattern"
      >
        <Grid size={16} />
      </ControlButton>
    </Controls>
  );
}

type VisualAgentflowBuilderProps = {
  agents: AgentDto[];
  agentflows?: AgentflowDto[];
  editingAgentflow?: AgentflowDetailDto | null;
  onAgentflowCreated?: () => void;
};

export function VisualAgentflowBuilder({
  agents,
  agentflows = [],
  editingAgentflow,
  onAgentflowCreated,
}: VisualAgentflowBuilderProps) {
  const [nodes, setNodes, onNodesChange] = useNodesState([]);
  const [edges, setEdges, onEdgesChange] = useEdgesState([]);
  const [selectedItemId, setSelectedItemId] = React.useState<string>("");
  const [pattern, setPattern] = React.useState<number>(-1); // Default to manual mode
  const [maximumIterationCount, setMaximumIterationCount] = React.useState<number>(5); // Group Chat parameter
  // Magentic pattern parameters
  const [maxRounds, setMaxRounds] = React.useState<number>(10);
  const [maxStallCount, setMaxStallCount] = React.useState<number>(3);
  const [maxResetCount, setMaxResetCount] = React.useState<number>(2);

  // agentflow creation states
  const [agentflowName, setAgentflowName] = React.useState("");
  const [agentflowDescription, setAgentflowDescription] = React.useState("");
  const [agentflowEnabled, setAgentflowEnabled] = React.useState(true);
  const [isCreating, setIsCreating] = React.useState(false);

  // Filter out current editing agentflow to prevent circular references
  const availableAgentflows = React.useMemo(() => {
    if (!editingAgentflow) return agentflows;
    return agentflows.filter((w) => w.id !== editingAgentflow.id);
  }, [agentflows, editingAgentflow]);

  const handleDeleteNode = React.useCallback(
    (nodeId: string) => {
      setNodes((nds) => nds.filter((node) => node.id !== nodeId));
      setEdges((eds) => eds.filter((edge) => edge.source !== nodeId && edge.target !== nodeId));
    },
    [setNodes, setEdges],
  );

  const handleRoleChange = React.useCallback(
    (nodeId: string, role: string) => {
      setNodes((nds) =>
        nds.map((node) => (node.id === nodeId ? { ...node, data: { ...node.data, role } } : node)),
      );
    },
    [setNodes],
  );

  // Load editing Agentflow data when present
  React.useEffect(() => {
    if (editingAgentflow) {
      // Set form fields
      setAgentflowName(editingAgentflow.name);
      setAgentflowDescription(editingAgentflow.description || "");
      setAgentflowEnabled(editingAgentflow.enable);
      setPattern(editingAgentflow.pattern);

      // Parse configuration for Group Chat pattern
      if (editingAgentflow.pattern === 2 && editingAgentflow.configurationJson) {
        try {
          const config = JSON.parse(editingAgentflow.configurationJson);
          setMaximumIterationCount(config.maximumIterationCount || 5);
        } catch {
          setMaximumIterationCount(5);
        }
      }

      // Parse configuration for Magentic pattern
      if (editingAgentflow.pattern === 4 && editingAgentflow.configurationJson) {
        try {
          const config = JSON.parse(editingAgentflow.configurationJson);
          setMaxRounds(config.maxRounds || 10);
          setMaxStallCount(config.maxStallCount || 3);
          setMaxResetCount(config.maxResetCount || 2);
        } catch {
          setMaxRounds(10);
          setMaxStallCount(3);
          setMaxResetCount(2);
        }
      }

      // Convert nodes to React Flow format
      const loadedNodes: Node[] = editingAgentflow.nodes.map((node: AgentflowNodeDto) => {
        if (node.type === AgentflowNodeType.AgentNode) {
          const agent = agents.find((a) => a.id === node.relateId);
          return {
            id: node.nodeId,
            type: "agentNode",
            position: { x: Math.random() * 400 + 200, y: Math.random() * 300 + 100 },
            data: {
              nodeId: node.nodeId,
              agentId: node.relateId,
              agentName: agent?.name || "Unknown Agent",
              role: "",
              onDelete: handleDeleteNode,
              onRoleChange: handleRoleChange,
            },
          };
        } else {
          const agentflow = agentflows.find((w) => w.id === node.relateId);
          return {
            id: node.nodeId,
            type: "agentflowNode",
            position: { x: Math.random() * 400 + 200, y: Math.random() * 300 + 100 },
            data: {
              nodeId: node.nodeId,
              agentflowId: node.relateId,
              agentflowName: agentflow?.name || "Unknown Agentflow",
              role: "",
              onDelete: handleDeleteNode,
              onRoleChange: handleRoleChange,
            },
          };
        }
      });

      // Convert edges to React Flow format
      const loadedEdges: Edge[] = editingAgentflow.edges.map((edge: AgentflowEdgeDto) => ({
        id: edge.edgeId,
        source: edge.sourceNodeId,
        target: edge.targetNodeId,
        animated: edge.animated ?? true,
        markerEnd: { type: MarkerType.ArrowClosed },
      }));

      setNodes(loadedNodes);
      setEdges(loadedEdges);
    }
  }, [editingAgentflow, agents, agentflows, handleDeleteNode, handleRoleChange]);

  const onConnect = React.useCallback(
    (params: Connection) => {
      setEdges((eds) =>
        addEdge(
          {
            ...params,
            animated: true,
            markerEnd: { type: MarkerType.ArrowClosed },
          },
          eds,
        ),
      );
    },
    [setEdges],
  );

  // Handle edge/node deletion via keyboard
  const onKeyDown = React.useCallback(
    (event: React.KeyboardEvent) => {
      if (event.key === "Delete" || event.key === "Backspace") {
        // Get selected elements from React Flow
        const selectedNodes = nodes.filter((node) => node.selected);
        const selectedEdges = edges.filter((edge) => edge.selected);

        // Delete selected edges
        if (selectedEdges.length > 0) {
          setEdges((eds) => eds.filter((edge) => !selectedEdges.find((se) => se.id === edge.id)));
        }

        // Delete selected nodes and their connected edges
        if (selectedNodes.length > 0) {
          const nodeIds = selectedNodes.map((node) => node.id);

          setNodes((nds) => nds.filter((node) => !nodeIds.includes(node.id)));
          setEdges((eds) =>
            eds.filter((edge) => !nodeIds.includes(edge.source) && !nodeIds.includes(edge.target)),
          );
        }
      }
    },
    [nodes, edges, setNodes, setEdges],
  );

  const addNode = React.useCallback(
    (combinedId: string) => {
      if (!combinedId) return;

      // Parse combined ID format: "agent:id" or "agentflow:id"
      const [type, itemId] = combinedId.split(":") as ["agent" | "agentflow", string];
      if (!type || !itemId) return;

      // Generate unique node ID to support duplicates
      const nodeId = `${type}-${itemId}-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`;

      let newNode: Node;

      if (type === "agent") {
        const agent = agents.find((a) => a.id === itemId);
        if (!agent) return;

        newNode = {
          id: nodeId,
          type: "agentNode",
          position: {
            x: Math.random() * 400 + 200,
            y: Math.random() * 300 + 100,
          },
          data: {
            nodeId: nodeId,
            agentId: agent.id,
            agentName: agent.name,
            role: "",
            onDelete: handleDeleteNode,
            onRoleChange: handleRoleChange,
          },
        };
      } else {
        // type === "agentflow"
        const agentflow = availableAgentflows.find((w) => w.id === itemId);
        if (!agentflow) return;

        newNode = {
          id: nodeId,
          type: "agentflowNode",
          position: {
            x: Math.random() * 400 + 200,
            y: Math.random() * 300 + 100,
          },
          data: {
            nodeId: nodeId,
            agentflowId: agentflow.id,
            agentflowName: agentflow.name,
            role: "",
            onDelete: handleDeleteNode,
            onRoleChange: handleRoleChange,
          },
        };
      }

      setNodes((nds) => [...nds, newNode]);
    },
    [agents, availableAgentflows, setNodes, handleDeleteNode, handleRoleChange],
  );

  // Auto-connect nodes based on selected pattern
  const handleAutoConnect = React.useCallback(
    (selectedPattern: number) => {
      // Get all nodes (no need to filter start node anymore)
      const agentNodes = nodes;

      if (agentNodes.length === 0) return;

      // Clear existing edges first
      setEdges([]);

      // If pattern is -1 (None), just clear edges and return
      if (selectedPattern === -1) {
        return;
      }

      const newEdges: Edge[] = [];

      switch (selectedPattern) {
        case 0: // Concurrent - no connections, all nodes run independently
          // No edges needed for concurrent execution
          break;

        case 1: // Sequential - create linear chain A→B→C
          // Connect nodes in sequence
          for (let i = 0; i < agentNodes.length - 1; i++) {
            newEdges.push({
              id: `e${agentNodes[i].id}-${agentNodes[i + 1].id}`,
              source: agentNodes[i].id,
              target: agentNodes[i + 1].id,
              animated: true,
              markerEnd: { type: MarkerType.ArrowClosed },
            });
          }
          break;

        case 2: // GroupChat - nodes connect in a circle
          // Connect nodes in a circle
          for (let i = 0; i < agentNodes.length; i++) {
            const nextIndex = (i + 1) % agentNodes.length;
            newEdges.push({
              id: `e${agentNodes[i].id}-${agentNodes[nextIndex].id}`,
              source: agentNodes[i].id,
              target: agentNodes[nextIndex].id,
              animated: true,
              markerEnd: { type: MarkerType.ArrowClosed },
            });
          }
          break;

        case 3: // Handoff - same as sequential (linear chain)
          // Connect nodes in sequence
          for (let i = 0; i < agentNodes.length - 1; i++) {
            newEdges.push({
              id: `e${agentNodes[i].id}-${agentNodes[i + 1].id}`,
              source: agentNodes[i].id,
              target: agentNodes[i + 1].id,
              animated: true,
              markerEnd: { type: MarkerType.ArrowClosed },
            });
          }
          break;

        case 4: // Magentic - orchestrator connects to workers (star topology)
          if (agentNodes.length > 1) {
            const orchestrator = agentNodes[0];
            const workers = agentNodes.slice(1);

            // Orchestrator ↔ Workers
            workers.forEach((worker) => {
              // Orchestrator → Worker
              newEdges.push({
                id: `e${orchestrator.id}-${worker.id}`,
                source: orchestrator.id,
                target: worker.id,
                animated: true,
                markerEnd: { type: MarkerType.ArrowClosed },
              });
              // Worker → Orchestrator (feedback loop)
              newEdges.push({
                id: `e${worker.id}-${orchestrator.id}`,
                source: worker.id,
                target: orchestrator.id,
                animated: true,
                markerEnd: { type: MarkerType.ArrowClosed },
              });
            });
          }
          // If only one agent, no edges needed
          break;
      }

      setEdges(newEdges);

      // Auto-layout nodes for better visualization
      handleAutoLayout(selectedPattern);
    },
    [nodes, setEdges],
  );

  // Auto-layout nodes based on pattern
  const handleAutoLayout = React.useCallback(
    async (_: number) => {
      if (nodes.length === 0) return;

      const result = await createGraphLayout(nodes, edges);
      setNodes(result.nodes);
      setEdges(result.edges);
    },
    [nodes, edges, setNodes, setEdges],
  );

  // Auto-detect pattern based on graph structure
  const detectPatternFromStructure = React.useCallback((): number => {
    if (nodes.length === 0) return 0;

    // No edges → Concurrent
    if (edges.length === 0) return 0;

    // Build in/out degree maps
    const inDegree = new Map<string, number>();
    const outDegree = new Map<string, number>();

    nodes.forEach((node) => {
      inDegree.set(node.id, 0);
      outDegree.set(node.id, 0);
    });

    edges.forEach((edge) => {
      inDegree.set(edge.target, (inDegree.get(edge.target) || 0) + 1);
      outDegree.set(edge.source, (outDegree.get(edge.source) || 0) + 1);
    });

    // Check for star topology (Magentic pattern)
    const potentialOrchestrators = nodes.filter(
      (node) => (outDegree.get(node.id) || 0) >= 2 && (inDegree.get(node.id) || 0) === 0,
    );

    if (potentialOrchestrators.length === 1) {
      const orchestrator = potentialOrchestrators[0];
      const workers = nodes.filter((n) => n.id !== orchestrator.id);

      const isStarTopology = workers.every((worker) => {
        const workerInDegree = inDegree.get(worker.id) || 0;
        const workerOutDegree = outDegree.get(worker.id) || 0;
        return workerInDegree <= 1 && workerOutDegree <= 1;
      });

      if (isStarTopology) return 4; // Magentic
    }

    // Check for linear chain (Sequential pattern)
    const isLinear = nodes.every(
      (node) => (inDegree.get(node.id) || 0) <= 1 && (outDegree.get(node.id) || 0) <= 1,
    );

    if (isLinear) {
      const startNodes = nodes.filter((n) => (inDegree.get(n.id) || 0) === 0);
      const endNodes = nodes.filter((n) => (outDegree.get(n.id) || 0) === 0);

      if (startNodes.length === 1 && endNodes.length === 1) {
        return 1; // Sequential
      }
    }

    // Default to Group Chat for complex topologies
    return 2;
  }, [nodes, edges]);

  const handleBuild = React.useCallback(async () => {
    // Validation
    if (!agentflowName.trim()) {
      toast.error("Please enter a agentflow name");
      return;
    }

    if (nodes.length === 0) {
      toast.error("Please add at least one node to the agentflow");
      return;
    }

    // If pattern is -1 (Manual), auto-detect the actual pattern from structure
    const effectivePattern = pattern === -1 ? detectPatternFromStructure() : pattern;

    // Convert ReactFlow nodes to AgentflowNode format
    const agentflowNodes = nodes.map((node) => ({
      nodeId: node.id,
      type:
        node.type === "agentNode" ? AgentflowNodeType.AgentNode : AgentflowNodeType.AgentflowNode,
      relateId: node.type === "agentNode" ? node.data.agentId : node.data.agentflowId,
    }));

    // Convert ReactFlow edges to AgentflowEdge format
    const agentflowEdges = edges.map((edge) => ({
      edgeId: edge.id,
      sourceNodeId: edge.source,
      targetNodeId: edge.target,
      animated: edge.animated ?? true,
    }));

    // Build configuration JSON based on pattern
    let configurationJson: string | null = null;
    if (effectivePattern === 2) {
      // Group Chat pattern
      configurationJson = JSON.stringify({ maximumIterationCount });
    } else if (effectivePattern === 4) {
      // Magentic pattern
      configurationJson = JSON.stringify({ maxRounds, maxStallCount, maxResetCount });
    }

    const requestBody = {
      name: agentflowName,
      description: agentflowDescription || null,
      pattern: effectivePattern,
      configurationJson,
      enable: agentflowEnabled,
      nodes: agentflowNodes,
      edges: agentflowEdges,
    };

    setIsCreating(true);
    try {
      if (editingAgentflow) {
        // Update existing agentflow
        // @ts-expect-error - Dynamic path not supported by apiPut types
        await apiPut(`/api/agentflows/${editingAgentflow.id}`, { body: requestBody });
        toast.success(`Agentflow "${agentflowName}" updated successfully!`);
      } else {
        // Create new agentflow
        await apiPost("/api/agentflows", { body: requestBody });
        toast.success(`Agentflow "${agentflowName}" created successfully!`);
      }

      // Reset form
      setAgentflowName("");
      setAgentflowDescription("");
      setAgentflowEnabled(true);
      setPattern(-1);
      setMaximumIterationCount(5);
      setMaxRounds(10);
      setMaxStallCount(3);
      setMaxResetCount(2);

      // Clear canvas
      setNodes([]);
      setEdges([]);

      // Notify parent
      onAgentflowCreated?.();
    } catch (error) {
      console.error("Failed to save agentflow:", error);
      toast.error(error instanceof Error ? error.message : "Failed to save agentflow");
    } finally {
      setIsCreating(false);
    }
  }, [
    nodes,
    edges,
    pattern,
    agentflowName,
    agentflowDescription,
    agentflowEnabled,
    maximumIterationCount,
    maxRounds,
    maxStallCount,
    maxResetCount,
    editingAgentflow,
    detectPatternFromStructure,
    setNodes,
    setEdges,
    onAgentflowCreated,
  ]);

  return (
    <Tabs defaultValue="basic" className="flex h-full w-full flex-col">
      <div className="flex items-center gap-4">
        <TabsList className="w-fit">
          <TabsTrigger value="basic">Basic</TabsTrigger>
          <TabsTrigger value="visual">Flow Editor</TabsTrigger>
        </TabsList>

        <Button
          onClick={handleBuild}
          disabled={isCreating || !agentflowName.trim() || nodes.length === 0}
          variant="default"
          className="ml-auto"
        >
          {isCreating
            ? editingAgentflow
              ? "Updating..."
              : "Creating..."
            : editingAgentflow
              ? "Update Agentflow"
              : "Build Agentflow"}
        </Button>
      </div>

      {/* Tab 1: basic */}
      <TabsContent value="basic" className="flex-1 space-y-4 mt-4">
        {/* Agentflow Details */}
        <div className="space-y-2">
          <Label htmlFor="agentflowName">Agentflow Name *</Label>
          <Input
            id="agentflowName"
            value={agentflowName}
            onChange={(e) => setAgentflowName(e.target.value)}
            placeholder="My Agentflow"
            className="max-w-md"
          />
        </div>

        <div className="space-y-2">
          <Label htmlFor="agentflowDescription">Description</Label>
          <Input
            id="agentflowDescription"
            value={agentflowDescription}
            onChange={(e) => setAgentflowDescription(e.target.value)}
            placeholder="Optional description"
            className="max-w-md"
          />
        </div>

        <div className="flex items-center gap-2">
          <Label htmlFor="agentflowEnabled" className="cursor-pointer">
            Enabled
          </Label>

          <Switch
            id="agentflowEnabled"
            checked={agentflowEnabled}
            onCheckedChange={setAgentflowEnabled}
          />
        </div>
      </TabsContent>

      {/* Tab 2: Visual Editor */}
      <TabsContent
        value="visual"
        className="flex-1 flex flex-col gap-4 min-h-0 mt-4"
        onKeyDown={onKeyDown}
        tabIndex={0}
      >
        {/* Controls */}
        <div className="flex flex-wrap items-end gap-4">
          {/* Add Node Selector */}
          <div className="space-y-2">
            <Label>Add Node</Label>
            <Select
              value={selectedItemId}
              onValueChange={(combinedId) => {
                addNode(combinedId);
                setSelectedItemId("");
              }}
            >
              <SelectTrigger className="w-68">
                <SelectValue placeholder="Choose an agent or agentflow..." />
              </SelectTrigger>
              <SelectContent position="popper" sideOffset={4}>
                <SelectGroup>
                  <SelectLabel>Agents</SelectLabel>
                  {agents.map((agent) => (
                    <SelectItem key={agent.id} value={`agent:${agent.id}`}>
                      {agent.name}
                    </SelectItem>
                  ))}
                </SelectGroup>
                <SelectGroup>
                  <SelectLabel>Agentflows</SelectLabel>
                  {availableAgentflows.length > 0 ? (
                    availableAgentflows.map((agentflow) => (
                      <SelectItem key={agentflow.id} value={`agentflow:${agentflow.id}`}>
                        {agentflow.name}
                      </SelectItem>
                    ))
                  ) : (
                    <SelectItem value="no-agentflows" disabled>
                      {editingAgentflow
                        ? "No other agentflows available"
                        : "No agentflows available"}
                    </SelectItem>
                  )}
                </SelectGroup>
              </SelectContent>
            </Select>
          </div>

          <div className="space-y-2">
            <Label>
              Auto-Connect
              <Tooltip>
                <TooltipTrigger asChild>
                  <Info size={16} className="text-muted-foreground" />
                </TooltipTrigger>
                <TooltipContent side="top">
                  <p>Select a pattern to auto-connect and layout nodes.</p>
                </TooltipContent>
              </Tooltip>
            </Label>
            <Select
              value={String(pattern)}
              onValueChange={(v) => {
                const newPattern = Number(v);
                setPattern(newPattern);
                handleAutoConnect(newPattern);
              }}
              disabled={nodes.length === 0}
            >
              <SelectTrigger className="w-[180px]">
                <SelectValue placeholder="Select pattern" />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="-1">None (Manual)</SelectItem>
                <SelectItem value="1">Sequential</SelectItem>
                <SelectItem value="3">Handoff</SelectItem>
                <SelectItem value="2">Group Chat</SelectItem>
                <SelectItem value="0">Concurrent</SelectItem>
                <SelectItem value="4">Magentic</SelectItem>
              </SelectContent>
            </Select>
          </div>

          {/* Group Chat Configuration */}
          {pattern === 2 && (
            <GroupChatConfig
              maximumIterationCount={maximumIterationCount}
              onMaximumIterationCountChange={setMaximumIterationCount}
            />
          )}

          {/* Magentic Pattern Configuration */}
          {pattern === 4 && (
            <MagenticConfig
              maxRounds={maxRounds}
              maxStallCount={maxStallCount}
              maxResetCount={maxResetCount}
              onMaxRoundsChange={setMaxRounds}
              onMaxStallCountChange={setMaxStallCount}
              onMaxResetCountChange={setMaxResetCount}
            />
          )}
        </div>

        {/* Canvas */}
        <div className="flex-1 min-h-0 rounded-lg border bg-muted/30">
          <ReactFlow
            nodes={nodes}
            edges={edges}
            onNodesChange={onNodesChange}
            onEdgesChange={onEdgesChange}
            onConnect={onConnect}
            nodeTypes={nodeTypes}
            fitView
            elementsSelectable={true}
            selectNodesOnDrag={false}
          >
            <FlowControls pattern={pattern} onAutoLayout={handleAutoLayout} />
            <Background variant={BackgroundVariant.Dots} gap={12} size={1} />
          </ReactFlow>
        </div>
        {/* Helper Text */}
        <div className="text-xs text-muted-foreground shrink-0">
          <p>
            <strong>Tip:</strong> Select agents or agentflows to add them to the canvas. Choose a
            agentflow pattern from the dropdown to automatically create connections and layout. You
            can also manually connect nodes by dragging from the{" "}
            <span className="inline-block w-2 h-2 bg-green-500 rounded-full"></span> green output
            handle to the <span className="inline-block w-2 h-2 bg-blue-500 rounded-full"></span>{" "}
            blue input handle of another node. Press{" "}
            <kbd className="px-1 py-0.5 text-xs bg-muted border rounded">Delete</kbd> or{" "}
            <kbd className="px-1 py-0.5 text-xs bg-muted border rounded">Backspace</kbd> to remove
            selected edges or nodes.
          </p>
          {pattern === -1 && (
            <p className="mt-1">
              <strong>None (Manual):</strong> Manually connect nodes by dragging edges. The
              agentflow pattern will be determined based on your connections.
            </p>
          )}
          {pattern === 0 && (
            <p className="mt-1">
              <strong>Concurrent:</strong> All nodes will run in parallel independently.
            </p>
          )}
          {pattern === 1 && (
            <p className="mt-1">
              <strong>Sequential:</strong> Nodes will execute in order following the edges.
            </p>
          )}
          {pattern === 2 && (
            <p className="mt-1">
              <strong>Group Chat:</strong> Nodes will collaborate in a managed conversation.
            </p>
          )}
          {pattern === 3 && (
            <p className="mt-1">
              <strong>Handoff:</strong> Control will be dynamically passed between nodes.
            </p>
          )}
          {pattern === 4 && (
            <p className="mt-1">
              <strong>Magentic:</strong> Central orchestrator coordinates multiple workers.
            </p>
          )}
        </div>
      </TabsContent>
    </Tabs>
  );
}