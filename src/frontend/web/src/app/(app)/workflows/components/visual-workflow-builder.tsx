"use client"

import * as React from "react"
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
} from "reactflow"
import "reactflow/dist/style.css"
import { Button } from "@/components/ui/button"
import { Label } from "@/components/ui/label"
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select"
import { Input } from "@/components/ui/input"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import { Info, Play, Workflow, Bot, Grid } from "lucide-react"
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip"
import { AgentDto, WorkflowDto, WorkflowNodeType } from "@/types/workflow"
import { toast } from "sonner"
import { apiPost, apiPut } from "@/api/client"

const START_NODE_ID = "__start_node__"


type AgentNodeData = {
  nodeId: string
  agentId: string
  agentName: string
  role: string
  onDelete: (id: string) => void
  onRoleChange: (id: string, role: string) => void
}

type WorkflowNodeData = {
  nodeId: string
  workflowId: string
  workflowName: string
  role: string
  onDelete: (id: string) => void
  onRoleChange: (id: string, role: string) => void
}

type StartNodeData = {
  // Start node has no editable data
}

// Start Node Component (fixed, non-deletable)
function StartNode({ data }: { data: StartNodeData }) {
  return (
    <Card className="min-w-20 shadow-lg p-0 gap-0  bg-green-50 border-green-300 rounded-full">
      <div className="p-2 flex items-center gap-2">
        <Play className="w-4 h-4 text-green-600" />
        <CardTitle className="text-sm font-semibold text-green-700">
          Start
        </CardTitle>
      </div>

      {/* Output Handle (Source) */}
      <Handle
        type="source"
        position={Position.Right}
        id="output"
        className="w-3 h-3 bg-green-600! border-2 border-white"
      />
    </Card>
  );
}

// Custom Workflow Node Component
function WorkflowNode({ data }: { data: WorkflowNodeData }) {
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
              {data.workflowName}
            </CardTitle>
          </div>
          <button
            onClick={() => data.onDelete(data.nodeId)}
            className="text-xs text-muted-foreground hover:text-destructive cursor-pointer"
            title="Remove workflow"
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
  )
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
          <CardTitle className="text-sm font-semibold">
            {data.agentName}
          </CardTitle>
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
  )
}

const nodeTypes: NodeTypes = {
  startNode: StartNode,
  agentNode: AgentNode,
  workflowNode: WorkflowNode,
}

type VisualWorkflowBuilderProps = {
  agents: AgentDto[]
  workflows?: WorkflowDto[]
  editingWorkflow?: {
    id: string
    name: string
    description: string | null
    pattern: number
    configurationJson: string | null
    enable: boolean
    nodes: any[]
    edges: any[]
  } | null
  onWorkflowCreated?: () => void
}

export function VisualWorkflowBuilder({
  agents,
  workflows = [],
  editingWorkflow,
  onWorkflowCreated,
}: VisualWorkflowBuilderProps) {
  // Initialize with start node
  const initialNodes: Node[] = [
    {
      id: START_NODE_ID,
      type: "startNode",
      position: { x: 50, y: 250 },
      data: {},
      draggable: true,
      selectable: false, // Cannot be selected for deletion
    }
  ]

  const [nodes, setNodes, onNodesChange] = useNodesState(initialNodes)
  const [edges, setEdges, onEdgesChange] = useEdgesState([])
  const [selectedNodeType, setSelectedNodeType] = React.useState<"agent" | "workflow">("agent")
  const [selectedItemId, setSelectedItemId] = React.useState<string>("")
  const [pattern, setPattern] = React.useState<number>(-1) // Default to manual mode
  const [maximumIterationCount, setMaximumIterationCount] = React.useState<number>(5) // Group Chat parameter

  // Workflow creation states
  const [workflowName, setWorkflowName] = React.useState("")
  const [workflowDescription, setWorkflowDescription] = React.useState("")
  const [workflowEnabled, setWorkflowEnabled] = React.useState(true)
  const [isCreating, setIsCreating] = React.useState(false)

  
  const handleDeleteNode = React.useCallback(
    (nodeId: string) => {
      // Prevent deleting start node
      if (nodeId === START_NODE_ID) return

      setNodes((nds) => nds.filter((node) => node.id !== nodeId))
      setEdges((eds) =>
        eds.filter((edge) => edge.source !== nodeId && edge.target !== nodeId)
      )
    },
    [setNodes, setEdges]
  )

  const handleRoleChange = React.useCallback(
    (nodeId: string, role: string) => {
      setNodes((nds) =>
        nds.map((node) =>
          node.id === nodeId
            ? { ...node, data: { ...node.data, role } }
            : node
        )
      )
    },
    [setNodes]
  )
  
  // Load editing workflow data when present
  React.useEffect(() => {
    if (editingWorkflow) {
      // Set form fields
      setWorkflowName(editingWorkflow.name)
      setWorkflowDescription(editingWorkflow.description || "")
      setWorkflowEnabled(editingWorkflow.enable)
      setPattern(editingWorkflow.pattern)

      // Parse configuration for Group Chat pattern
      if (editingWorkflow.pattern === 2 && editingWorkflow.configurationJson) {
        try {
          const config = JSON.parse(editingWorkflow.configurationJson)
          setMaximumIterationCount(config.maximumIterationCount || 5)
        } catch {
          setMaximumIterationCount(5)
        }
      }

      // Convert nodes to React Flow format
      const loadedNodes: Node[] = [
        // Always include start node
        {
          id: START_NODE_ID,
          type: "startNode",
          position: { x: 50, y: 250 },
          data: {},
          draggable: true,
          selectable: false,
        },
        // Add workflow nodes
        ...editingWorkflow.nodes.map((node: any) => {
          if (node.type === WorkflowNodeType.AgentNode) {
            const agent = agents.find(a => a.id === node.relateId)
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
            }
          } else {
            const workflow = workflows.find(w => w.id === node.relateId)
            return {
              id: node.nodeId,
              type: "workflowNode",
              position: { x: Math.random() * 400 + 200, y: Math.random() * 300 + 100 },
              data: {
                nodeId: node.nodeId,
                workflowId: node.relateId,
                workflowName: workflow?.name || "Unknown Workflow",
                role: "",
                onDelete: handleDeleteNode,
                onRoleChange: handleRoleChange,
              },
            }
          }
        })
      ]

      // Convert edges to React Flow format
      const loadedEdges: Edge[] = editingWorkflow.edges.map((edge: any) => ({
        id: edge.edgeId,
        source: edge.sourceNodeId,
        target: edge.targetNodeId,
        animated: edge.animated ?? true,
        markerEnd: { type: MarkerType.ArrowClosed },
      }))

      setNodes(loadedNodes)
      setEdges(loadedEdges)
    }
  }, [editingWorkflow, agents, workflows, handleDeleteNode, handleRoleChange])

  const onConnect = React.useCallback(
    (params: Connection) => {
      setEdges((eds) =>
        addEdge(
          {
            ...params,
            animated: true,
            markerEnd: { type: MarkerType.ArrowClosed },
          },
          eds
        )
      )
    },
    [setEdges]
  )

  // Handle edge/node deletion via keyboard
  const onKeyDown = React.useCallback(
    (event: React.KeyboardEvent) => {
      if (event.key === 'Delete' || event.key === 'Backspace') {
        // Get selected elements from React Flow
        const selectedNodes = nodes.filter((node) => node.selected)
        const selectedEdges = edges.filter((edge) => edge.selected)

        // Delete selected edges
        if (selectedEdges.length > 0) {
          setEdges((eds) =>
            eds.filter((edge) => !selectedEdges.find((se) => se.id === edge.id))
          )
        }

        // Delete selected nodes (except start node) and their connected edges
        if (selectedNodes.length > 0) {
          const nodeIds = selectedNodes
            .filter((node) => node.id !== START_NODE_ID) // Don't delete start node
            .map((node) => node.id)

          if (nodeIds.length > 0) {
            setNodes((nds) => nds.filter((node) => !nodeIds.includes(node.id)))
            setEdges((eds) =>
              eds.filter(
                (edge) => !nodeIds.includes(edge.source) && !nodeIds.includes(edge.target)
              )
            )
          }
        }
      }
    },
    [nodes, edges, setNodes, setEdges]
  )


  const addNode = React.useCallback((type: "agent" | "workflow", itemId: string) => {
    if (!itemId) return

    // Generate unique node ID to support duplicates
    const nodeId = `${type}-${itemId}-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`

    let newNode: Node

    if (type === "agent") {
      const agent = agents.find((a) => a.id === itemId)
      if (!agent) return

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
      }
    } else {
      // type === "workflow"
      const workflow = workflows.find((w) => w.id === itemId)
      if (!workflow) return

      newNode = {
        id: nodeId,
        type: "workflowNode",
        position: {
          x: Math.random() * 400 + 200,
          y: Math.random() * 300 + 100,
        },
        data: {
          nodeId: nodeId,
          workflowId: workflow.id,
          workflowName: workflow.name,
          role: "",
          onDelete: handleDeleteNode,
          onRoleChange: handleRoleChange,
        },
      }
    }

    setNodes((nds) => [...nds, newNode])
  }, [agents, workflows, setNodes, handleDeleteNode, handleRoleChange])

  // Auto-connect nodes based on selected pattern
  const handleAutoConnect = React.useCallback((selectedPattern: number) => {
    // Filter out start node to get only agent/workflow nodes
    const agentNodes = nodes.filter(node => node.id !== START_NODE_ID)

    if (agentNodes.length === 0) return

    // Clear existing edges first
    setEdges([])

    // If pattern is -1 (None), just clear edges and return
    if (selectedPattern === -1) {
      return
    }

    const newEdges: Edge[] = []

    switch (selectedPattern) {
      case 0: // Concurrent - start connects to all nodes
        agentNodes.forEach(node => {
          newEdges.push({
            id: `e${START_NODE_ID}-${node.id}`,
            source: START_NODE_ID,
            target: node.id,
            animated: true,
            markerEnd: { type: MarkerType.ArrowClosed },
          })
        })
        break

      case 1: // Sequential - create linear chain Start→A→B→C
        // Connect start to first node
        if (agentNodes.length > 0) {
          newEdges.push({
            id: `e${START_NODE_ID}-${agentNodes[0].id}`,
            source: START_NODE_ID,
            target: agentNodes[0].id,
            animated: true,
            markerEnd: { type: MarkerType.ArrowClosed },
          })
        }
        // Connect nodes in sequence
        for (let i = 0; i < agentNodes.length - 1; i++) {
          newEdges.push({
            id: `e${agentNodes[i].id}-${agentNodes[i + 1].id}`,
            source: agentNodes[i].id,
            target: agentNodes[i + 1].id,
            animated: true,
            markerEnd: { type: MarkerType.ArrowClosed },
          })
        }
        break

      case 2: // GroupChat - start connects to all, nodes connect in a circle
        // Connect start to all nodes
        agentNodes.forEach(node => {
          newEdges.push({
            id: `e${START_NODE_ID}-${node.id}`,
            source: START_NODE_ID,
            target: node.id,
            animated: true,
            markerEnd: { type: MarkerType.ArrowClosed },
          })
        })
        // Connect nodes in a circle
        for (let i = 0; i < agentNodes.length; i++) {
          const nextIndex = (i + 1) % agentNodes.length
          newEdges.push({
            id: `e${agentNodes[i].id}-${agentNodes[nextIndex].id}`,
            source: agentNodes[i].id,
            target: agentNodes[nextIndex].id,
            animated: true,
            markerEnd: { type: MarkerType.ArrowClosed },
          })
        }
        break

      case 3: // Handoff - same as sequential (linear chain)
        // Connect start to first node
        if (agentNodes.length > 0) {
          newEdges.push({
            id: `e${START_NODE_ID}-${agentNodes[0].id}`,
            source: START_NODE_ID,
            target: agentNodes[0].id,
            animated: true,
            markerEnd: { type: MarkerType.ArrowClosed },
          })
        }
        // Connect nodes in sequence
        for (let i = 0; i < agentNodes.length - 1; i++) {
          newEdges.push({
            id: `e${agentNodes[i].id}-${agentNodes[i + 1].id}`,
            source: agentNodes[i].id,
            target: agentNodes[i + 1].id,
            animated: true,
            markerEnd: { type: MarkerType.ArrowClosed },
          })
        }
        break

      case 4: // Magentic - start to orchestrator, orchestrator connects to workers (star topology)
        if (agentNodes.length > 1) {
          const orchestrator = agentNodes[0]
          const workers = agentNodes.slice(1)

          // Start → Orchestrator
          newEdges.push({
            id: `e${START_NODE_ID}-${orchestrator.id}`,
            source: START_NODE_ID,
            target: orchestrator.id,
            animated: true,
            markerEnd: { type: MarkerType.ArrowClosed },
          })

          // Orchestrator ↔ Workers
          workers.forEach(worker => {
            // Orchestrator → Worker
            newEdges.push({
              id: `e${orchestrator.id}-${worker.id}`,
              source: orchestrator.id,
              target: worker.id,
              animated: true,
              markerEnd: { type: MarkerType.ArrowClosed },
            })
            // Worker → Orchestrator (feedback loop)
            newEdges.push({
              id: `e${worker.id}-${orchestrator.id}`,
              source: worker.id,
              target: orchestrator.id,
              animated: true,
              markerEnd: { type: MarkerType.ArrowClosed },
            })
          })
        } else if (agentNodes.length === 1) {
          // Only one agent, just connect start to it
          newEdges.push({
            id: `e${START_NODE_ID}-${agentNodes[0].id}`,
            source: START_NODE_ID,
            target: agentNodes[0].id,
            animated: true,
            markerEnd: { type: MarkerType.ArrowClosed },
          })
        }
        break
    }

    setEdges(newEdges)

    // Auto-layout nodes for better visualization
    handleAutoLayout(selectedPattern)
  }, [nodes, setEdges])

  // Auto-layout nodes based on pattern
  const handleAutoLayout = React.useCallback((selectedPattern: number) => {
    if (nodes.length === 0) return

    const layoutNodes = [...nodes]
    const canvasWidth = 800
    const canvasHeight = 600
    const padding = 100
    const startX = 50
    const agentStartX = 200 // Agent/workflow nodes start from this X position

    // Filter agent/workflow nodes for layout calculation
    const agentNodes = layoutNodes.filter(n => n.id !== START_NODE_ID)
    const startNode = layoutNodes.find(n => n.id === START_NODE_ID)

    // Position start node on the left center
    if (startNode) {
      startNode.position = { x: startX, y: canvasHeight / 2 }
    }

    switch (selectedPattern) {
      case 0: // Concurrent - grid layout for nodes
        const cols = Math.ceil(Math.sqrt(agentNodes.length))
        const rows = Math.ceil(agentNodes.length / cols)
        const spacingX = (canvasWidth - agentStartX - padding) / Math.max(1, cols - 1)
        const spacingY = (canvasHeight - 2 * padding) / Math.max(1, rows - 1)

        agentNodes.forEach((node, i) => {
          const col = i % cols
          const row = Math.floor(i / cols)
          node.position = {
            x: agentStartX + col * (cols === 1 ? 0 : spacingX),
            y: padding + row * (rows === 1 ? 0 : spacingY),
          }
        })
        break

      case 1: // Sequential - horizontal line
      case 3: // Handoff - horizontal line
        const seqSpacing = (canvasWidth - agentStartX - padding) / Math.max(1, agentNodes.length - 1)
        agentNodes.forEach((node, i) => {
          node.position = {
            x: agentStartX + i * (agentNodes.length === 1 ? 0 : seqSpacing),
            y: canvasHeight / 2,
          }
        })
        break

      case 2: // GroupChat - circular layout for nodes
        const radius = Math.min(canvasWidth - agentStartX, canvasHeight) / 3
        const centerX = (canvasWidth + agentStartX) / 2
        const centerY = canvasHeight / 2
        const angleStep = (2 * Math.PI) / agentNodes.length

        agentNodes.forEach((node, i) => {
          const angle = i * angleStep - Math.PI / 2 // Start from top
          node.position = {
            x: centerX + radius * Math.cos(angle),
            y: centerY + radius * Math.sin(angle),
          }
        })
        break

      case 4: // Magentic - star topology (orchestrator in center)
        if (agentNodes.length === 1) {
          agentNodes[0].position = { x: (canvasWidth + agentStartX) / 2, y: canvasHeight / 2 }
        } else if (agentNodes.length > 1) {
          const centerX = (canvasWidth + agentStartX) / 2
          const centerY = canvasHeight / 2

          // Orchestrator in center
          agentNodes[0].position = { x: centerX, y: centerY }

          // Workers in circle around orchestrator
          const workerRadius = Math.min(canvasWidth - agentStartX, canvasHeight) / 3
          const workerAngleStep = (2 * Math.PI) / (agentNodes.length - 1)

          for (let i = 1; i < agentNodes.length; i++) {
            const angle = (i - 1) * workerAngleStep - Math.PI / 2
            agentNodes[i].position = {
              x: centerX + workerRadius * Math.cos(angle),
              y: centerY + workerRadius * Math.sin(angle),
            }
          }
        }
        break
    }

    setNodes(layoutNodes)
  }, [nodes, setNodes])

  // Auto-detect pattern based on graph structure
  const detectPatternFromStructure = React.useCallback((): number => {
    // Filter out start node for pattern detection
    const agentNodes = nodes.filter(node => node.id !== START_NODE_ID)

    if (agentNodes.length === 0) return 0

    // Filter edges to only include those between agent/workflow nodes
    const agentEdges = edges.filter(
      edge => edge.source !== START_NODE_ID && edge.target !== START_NODE_ID
    )

    // No edges → Concurrent
    if (agentEdges.length === 0) return 0

    // Build in/out degree maps
    const inDegree = new Map<string, number>()
    const outDegree = new Map<string, number>()

    agentNodes.forEach(node => {
      inDegree.set(node.id, 0)
      outDegree.set(node.id, 0)
    })

    agentEdges.forEach(edge => {
      inDegree.set(edge.target, (inDegree.get(edge.target) || 0) + 1)
      outDegree.set(edge.source, (outDegree.get(edge.source) || 0) + 1)
    })

    // Check for star topology (Magentic pattern)
    const potentialOrchestrators = agentNodes.filter(node =>
      (outDegree.get(node.id) || 0) >= 2 && (inDegree.get(node.id) || 0) === 0
    )

    if (potentialOrchestrators.length === 1) {
      const orchestrator = potentialOrchestrators[0]
      const workers = agentNodes.filter(n => n.id !== orchestrator.id)

      const isStarTopology = workers.every(worker => {
        const workerInDegree = inDegree.get(worker.id) || 0
        const workerOutDegree = outDegree.get(worker.id) || 0
        return workerInDegree <= 1 && workerOutDegree <= 1
      })

      if (isStarTopology) return 4 // Magentic
    }

    // Check for linear chain (Sequential pattern)
    const isLinear = agentNodes.every(node =>
      (inDegree.get(node.id) || 0) <= 1 && (outDegree.get(node.id) || 0) <= 1
    )

    if (isLinear) {
      const startNodes = agentNodes.filter(n => (inDegree.get(n.id) || 0) === 0)
      const endNodes = agentNodes.filter(n => (outDegree.get(n.id) || 0) === 0)

      if (startNodes.length === 1 && endNodes.length === 1) {
        return 1 // Sequential
      }
    }

    // Default to Group Chat for complex topologies
    return 2
  }, [nodes, edges])

  const handleBuild = React.useCallback(async () => {
    // Validation
    if (!workflowName.trim()) {
      toast.error("Please enter a workflow name")
      return
    }

    const allNodes = nodes.filter(node => node.id !== START_NODE_ID)
    if (allNodes.length === 0) {
      toast.error("Please add at least one node to the workflow")
      return
    }

    // If pattern is -1 (Manual), auto-detect the actual pattern from structure
    const effectivePattern = pattern === -1 ? detectPatternFromStructure() : pattern

    // Convert ReactFlow nodes to WorkflowNode format
    const workflowNodes = allNodes.map(node => ({
      nodeId: node.id,
      type: node.type === "agentNode" ? WorkflowNodeType.AgentNode : WorkflowNodeType.WorkflowNode,
      relateId: node.type === "agentNode" ? node.data.agentId : node.data.workflowId,
    }))

    // Convert ReactFlow edges to WorkflowEdge format (exclude edges from/to start node)
    const workflowEdges = edges
      .filter(edge => edge.source !== START_NODE_ID && edge.target !== START_NODE_ID)
      .map(edge => ({
        edgeId: edge.id,
        sourceNodeId: edge.source,
        targetNodeId: edge.target,
        animated: edge.animated ?? true,
      }))

    // Build configuration JSON based on pattern
    let configurationJson: string | null = null
    if (effectivePattern === 2) {
      // Group Chat pattern
      configurationJson = JSON.stringify({ maximumIterationCount })
    }

    const requestBody = {
      name: workflowName,
      description: workflowDescription || null,
      pattern: effectivePattern,
      configurationJson,
      enable: workflowEnabled,
      nodes: workflowNodes,
      edges: workflowEdges,
    }

    setIsCreating(true)
    try {
      if (editingWorkflow) {
        // Update existing workflow
        await apiPut(`/api/workflows/${editingWorkflow.id}`, { body: requestBody })
        toast.success(`Workflow "${workflowName}" updated successfully!`)
      } else {
        // Create new workflow
        await apiPost("/api/workflows", { body: requestBody })
        toast.success(`Workflow "${workflowName}" created successfully!`)
      }

      // Reset form
      setWorkflowName("")
      setWorkflowDescription("")
      setWorkflowEnabled(true)
      setPattern(-1)
      setMaximumIterationCount(5)

      // Clear canvas
      setNodes(initialNodes)
      setEdges([])

      // Notify parent
      onWorkflowCreated?.()
    } catch (error) {
      console.error("Failed to save workflow:", error)
      toast.error(error instanceof Error ? error.message : "Failed to save workflow")
    } finally {
      setIsCreating(false)
    }
  }, [
    nodes,
    edges,
    pattern,
    workflowName,
    workflowDescription,
    workflowEnabled,
    maximumIterationCount,
    editingWorkflow,
    detectPatternFromStructure,
    setNodes,
    setEdges,
    onWorkflowCreated,
  ])

  return (
    <div
      className="flex h-full w-full flex-col gap-4"
      onKeyDown={onKeyDown}
      tabIndex={0}
    >
      {/* Controls */}
      <div className="flex flex-wrap items-end gap-4">
        {/* Node Type Selector */}
        <div className="space-y-2">
          <Label>Node Type</Label>
          <Select
            value={selectedNodeType}
            onValueChange={(value) => {
              setSelectedNodeType(value as "agent" | "workflow")
              setSelectedItemId("") // Reset selection when switching type
            }}
          >
            <SelectTrigger className="w-[120px]">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="agent">Agent</SelectItem>
              <SelectItem value="workflow">Workflow</SelectItem>
            </SelectContent>
          </Select>
        </div>

        {/* Item Selector (Agent or Workflow) */}
        <div className="space-y-2">
          <Label>
            {selectedNodeType === "agent" ? "Select Agent" : "Select Workflow"}
          </Label>
          <Select
            value={selectedItemId}
            onValueChange={(itemId) => {
              addNode(selectedNodeType, itemId)
              setSelectedItemId("")
            }}
          >
            <SelectTrigger className="w-50">
              <SelectValue
                placeholder={
                  selectedNodeType === "agent"
                    ? "Choose an agent..."
                    : "Choose a workflow..."
                }
              />
            </SelectTrigger>
            <SelectContent>
              {selectedNodeType === "agent"
                ? agents.map((agent) => (
                    <SelectItem key={agent.id} value={agent.id}>
                      {agent.name}
                    </SelectItem>
                  ))
                : workflows.map((workflow) => (
                    <SelectItem key={workflow.id} value={workflow.id}>
                      {workflow.name}
                    </SelectItem>
                  ))}
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
                <p>
                  Select a pattern to auto-connect and layout nodes.
                </p>
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
          <div className="space-y-2">
            <Label>
              Max Iterations
              <Tooltip>
                <TooltipTrigger asChild>
                  <Info size={16} className="text-muted-foreground ml-1 inline" />
                </TooltipTrigger>
                <TooltipContent side="top">
                  <p>
                    Maximum number of iterations for Group Chat pattern
                  </p>
                </TooltipContent>
              </Tooltip>
            </Label>
            <Input
              type="number"
              min="1"
              max="100"
              value={maximumIterationCount}
              onChange={(e) => setMaximumIterationCount(Number(e.target.value))}
              className="w-[120px]"
            />
          </div>
        )}

        {/* Workflow Details */}
        <div className="space-y-2">
          <Label htmlFor="workflowName">Workflow Name *</Label>
          <Input
            id="workflowName"
            value={workflowName}
            onChange={(e) => setWorkflowName(e.target.value)}
            placeholder="My Workflow"
            className="w-[200px]"
          />
        </div>

        <div className="space-y-2">
          <Label htmlFor="workflowDescription">Description</Label>
          <Input
            id="workflowDescription"
            value={workflowDescription}
            onChange={(e) => setWorkflowDescription(e.target.value)}
            placeholder="Optional description"
            className="w-[250px]"
          />
        </div>

        <div className="flex items-center gap-2 pt-6">
          <input
            id="workflowEnabled"
            type="checkbox"
            checked={workflowEnabled}
            onChange={(e) => setWorkflowEnabled(e.target.checked)}
            className="cursor-pointer"
          />
          <Label htmlFor="workflowEnabled" className="cursor-pointer">
            Enabled
          </Label>
        </div>

        <Button
          onClick={handleBuild}
          disabled={isCreating || !workflowName.trim() || nodes.filter(n => n.id !== START_NODE_ID).length === 0}
          variant="default"
        >
          {isCreating
            ? (editingWorkflow ? "Updating..." : "Creating...")
            : (editingWorkflow ? "Update Workflow" : "Build Workflow")}
        </Button>
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
          <Controls>
            <ControlButton
              onClick={() => handleAutoLayout(pattern)}
              title="Auto Layout - Arrange nodes based on current pattern"
            >
              <Grid size={16} />
            </ControlButton>
          </Controls>
          <Background variant={BackgroundVariant.Dots} gap={12} size={1} />
        </ReactFlow>
      </div>
            {/* Helper Text */}
      <div className="text-xs text-muted-foreground flex-shrink-0">
        <p>
          <strong>Tip:</strong> Select agents or workflows to add them to the canvas. Choose
          a workflow pattern from the dropdown to automatically create
          connections and layout. You can also manually connect nodes by
          dragging from the{" "}
          <span className="inline-block w-2 h-2 bg-green-500 rounded-full"></span>{" "}
          green output handle to the{" "}
          <span className="inline-block w-2 h-2 bg-blue-500 rounded-full"></span>{" "}
          blue input handle of another node. Press{" "}
          <kbd className="px-1 py-0.5 text-xs bg-muted border rounded">
            Delete
          </kbd>{" "}
          or{" "}
          <kbd className="px-1 py-0.5 text-xs bg-muted border rounded">
            Backspace
          </kbd>{" "}
          to remove selected edges or nodes.
        </p>
        {pattern === -1 && (
          <p className="mt-1">
            <strong>None (Manual):</strong> Manually connect nodes by dragging
            edges. The workflow pattern will be determined based on your
            connections.
          </p>
        )}
        {pattern === 0 && (
          <p className="mt-1">
            <strong>Concurrent:</strong> All nodes will run in parallel
            independently.
          </p>
        )}
        {pattern === 1 && (
          <p className="mt-1">
            <strong>Sequential:</strong> Nodes will execute in order following
            the edges.
          </p>
        )}
        {pattern === 2 && (
          <p className="mt-1">
            <strong>Group Chat:</strong> Nodes will collaborate in a managed
            conversation.
          </p>
        )}
        {pattern === 3 && (
          <p className="mt-1">
            <strong>Handoff:</strong> Control will be dynamically passed between
            nodes.
          </p>
        )}
        {pattern === 4 && (
          <p className="mt-1">
            <strong>Magentic:</strong> Central orchestrator coordinates multiple
            workers.
          </p>
        )}
      </div>
    </div>
  );
}

// Topological sort helper for sequential workflows
function topologicalSort(nodes: Node[], edges: Edge[]): Node[] {
  const adjList = new Map<string, string[]>()
  const inDegree = new Map<string, number>()

  // Initialize
  nodes.forEach((node) => {
    adjList.set(node.id, [])
    inDegree.set(node.id, 0)
  })

  // Build adjacency list and in-degree count
  edges.forEach((edge) => {
    adjList.get(edge.source)?.push(edge.target)
    inDegree.set(edge.target, (inDegree.get(edge.target) || 0) + 1)
  })

  // Find all nodes with in-degree 0
  const queue: string[] = []
  nodes.forEach((node) => {
    if (inDegree.get(node.id) === 0) {
      queue.push(node.id)
    }
  })

  const sorted: Node[] = []

  while (queue.length > 0) {
    const nodeId = queue.shift()!
    const node = nodes.find((n) => n.id === nodeId)
    if (node) sorted.push(node)

    const neighbors = adjList.get(nodeId) || []
    neighbors.forEach((neighbor) => {
      const degree = inDegree.get(neighbor)! - 1
      inDegree.set(neighbor, degree)
      if (degree === 0) {
        queue.push(neighbor)
      }
    })
  }

  // If sorted length doesn't match nodes length, there's a cycle
  // In that case, fall back to original node order
  if (sorted.length !== nodes.length) {
    return nodes
  }

  return sorted
}
