import { AGENTFLOW_NODE_LABELS } from "@agw/chat-core";
import {
  type AgentflowBlockMember,
  type AgentflowDefinition,
  type AgentflowDto,
  type AgentflowEdge,
  type AgentflowNode,
  type AgentflowNodeKind as AgentflowNodeKindType,
  type AgentflowPosition,
} from "../../../../types/agentflow";
import { getNewEdgeRouting } from "./agentflow-edge-routing";
import { AGENTFLOW_NODE_SIZE } from "./agentflow-canvas-geometry";

export type AgentflowEditorDocument = {
  name: string;
  description: string;
  nodes: AgentflowNode[];
  edges: AgentflowEdge[];
};

export type NodeReference = {
  kind: "agent" | "workflowAsAgent";
  relateId: string;
  name: string;
};

const isBlockKind = (kind: AgentflowNodeKindType) =>
  kind === "concurrentBlock" ||
  kind === "handoffBlock" ||
  kind === "groupChatBlock" ||
  kind === "magenticBlock";

export function createId(prefix: string): string {
  return `${prefix}-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
}

export function createEmptyDocument(): AgentflowEditorDocument {
  return {
    name: "",
    description: "",
    nodes: [
      {
        kind: "input",
        nodeId: "input",
        name: "Input",
        position: { x: 80, y: 80 },
        routingStrategy: "direct",
      },
    ],
    edges: [],
  };
}

export function createEditorDocument(
  agentflow: Pick<AgentflowDto, "name" | "description">,
  definition: AgentflowDefinition,
): AgentflowEditorDocument {
  return {
    name: agentflow.name,
    description: agentflow.description ?? "",
    nodes: definition.nodes.map((node) => ({ ...node, position: { ...node.position } })),
    edges: definition.edges.map((edge) => ({
      ...edge,
      conditions: edge.conditions.map((rule) => ({ ...rule })),
    })),
  };
}

export function createNode(
  kind: AgentflowNodeKindType,
  position: AgentflowPosition,
  reference?: NodeReference,
): AgentflowNode {
  const nodeId = createId(kind);
  const shared = {
    nodeId,
    name: reference?.name ?? AGENTFLOW_NODE_LABELS[kind],
    position,
    routingStrategy: "direct" as const,
  };

  switch (kind) {
    case "input":
      return { ...shared, kind, nodeId: "input", name: "Input" };
    case "agent":
    case "workflowAsAgent":
      return {
        ...shared,
        kind,
        routingStrategy: "direct",
        relateId: reference?.relateId ?? null,
        instructions: "",
      };
    case "promptAdapter":
      return { ...shared, kind, instructions: "" };
    case "clearMessages":
      return { ...shared, kind };
    case "humanGate":
      return {
        ...shared,
        kind,
        routingStrategy: "choice",
        humanPrompt: null,
        choices: [{ choiceId: createId("choice"), label: "Continue" }],
        allowResponseText: false,
      };
    case "checkpointMarker":
      return { ...shared, kind, checkpointName: null };
    case "output":
      return {
        ...shared,
        kind,
        enableSummary: false,
        summaryModelProviderId: null,
        instructions: null,
      };
    case "concurrentBlock":
      return { ...shared, kind, instructions: "", members: [] };
    case "handoffBlock":
      return {
        ...shared,
        kind,
        instructions: "",
        members: [],
        handoffInstructions: null,
        enableReturnToPrevious: null,
        autonomous: null,
        autonomousTurnLimit: null,
        continuationPrompt: null,
      };
    case "groupChatBlock":
      return { ...shared, kind, instructions: "", members: [], maxRounds: null };
    case "magenticBlock":
      return {
        ...shared,
        kind,
        instructions: "",
        members: [],
        maxRounds: null,
        maxStalls: null,
        maxResets: null,
        requirePlanSignoff: null,
      };
  }
}

export function createEdge(
  document: AgentflowEditorDocument,
  sourceNodeId: string,
  targetNodeId: string,
): AgentflowEdge {
  return {
    edgeId: createId("edge"),
    sourceNodeId,
    targetNodeId,
    isFanInBarrier: false,
    isSwitchDefault: false,
    ...getNewEdgeRouting(document, sourceNodeId),
    label: null,
    conditions: [],
  };
}

export function insertNodeOnEdge(
  document: AgentflowEditorDocument,
  edgeId: string,
  node: AgentflowNode,
): AgentflowEditorDocument {
  const edge = document.edges.find((item) => item.edgeId === edgeId);
  if (!edge) return document;

  const firstEdge: AgentflowEdge = edge.isFanInBarrier
    ? {
        ...edge,
        targetNodeId: node.nodeId,
        isFanInBarrier: false,
        isSwitchDefault: false,
        switchCaseOrder: 0,
        choiceId: null,
        conditions: [],
      }
    : { ...edge, targetNodeId: node.nodeId };
  const createdEdge = createEdge(
    { ...document, nodes: [...document.nodes, node] },
    node.nodeId,
    edge.targetNodeId,
  );
  const secondEdge = edge.isFanInBarrier
    ? {
        ...createdEdge,
        isFanInBarrier: true,
        isSwitchDefault: false,
        switchCaseOrder: 0,
        choiceId: null,
        conditions: [],
      }
    : createdEdge;

  return {
    ...document,
    nodes: [...document.nodes, node],
    edges: document.edges.flatMap((item) =>
      item.edgeId === edgeId ? [firstEdge, secondEdge] : [item],
    ),
  };
}

export function retargetEdge(
  document: AgentflowEditorDocument,
  edgeId: string,
  targetNodeId: string,
): AgentflowEditorDocument {
  return {
    ...document,
    edges: document.edges.map((edge) =>
      edge.edgeId === edgeId ? { ...edge, targetNodeId } : edge,
    ),
  };
}

export function isValidConnection(
  document: AgentflowEditorDocument,
  sourceNodeId: string,
  targetNodeId: string,
): boolean {
  const source = document.nodes.find((node) => node.nodeId === sourceNodeId);
  const target = document.nodes.find((node) => node.nodeId === targetNodeId);
  return Boolean(
    source &&
    target &&
    target.kind !== "input" &&
    sourceNodeId !== targetNodeId &&
    !document.edges.some(
      (edge) => edge.sourceNodeId === sourceNodeId && edge.targetNodeId === targetNodeId,
    ),
  );
}

export function addMember(
  document: AgentflowEditorDocument,
  nodeId: string,
  reference: NodeReference,
): AgentflowEditorDocument {
  const member: AgentflowBlockMember = {
    memberId: createId("member"),
    kind: reference.kind,
    relateId: reference.relateId,
    name: reference.name,
    instructions: null,
    isManager: false,
  };

  return {
    ...document,
    nodes: document.nodes.map((node) => {
      if (node.nodeId !== nodeId || !("members" in node) || !isBlockKind(node.kind)) return node;
      const members = [
        ...node.members,
        { ...member, isManager: node.kind === "magenticBlock" && node.members.length === 0 },
      ];
      return { ...node, members };
    }),
  };
}

export function removeMember(
  document: AgentflowEditorDocument,
  nodeId: string,
  memberId: string,
): AgentflowEditorDocument {
  return {
    ...document,
    nodes: document.nodes.map((node) => {
      if (node.nodeId !== nodeId || !("members" in node) || !isBlockKind(node.kind)) return node;
      const removedManager = node.members.some(
        (member) => member.memberId === memberId && member.isManager,
      );
      const members = node.members
        .filter((member) => member.memberId !== memberId)
        .map((member, index) => ({
          ...member,
          isManager:
            node.kind === "magenticBlock" && (removedManager ? index === 0 : member.isManager),
        }));
      return { ...node, members };
    }),
  };
}

export function replaceEditorGraph(
  document: AgentflowEditorDocument,
  graph: Pick<AgentflowEditorDocument, "nodes" | "edges">,
): AgentflowEditorDocument {
  return { ...document, nodes: graph.nodes, edges: graph.edges };
}

export function nodeOverlapsAnother(
  document: AgentflowEditorDocument,
  nodeId: string,
  sizesById: Record<string, { width: number; height: number }>,
): boolean {
  const node = document.nodes.find((item) => item.nodeId === nodeId);
  if (!node) return false;

  const size = sizesById[node.nodeId] ?? AGENTFLOW_NODE_SIZE;
  const left = Number(node.position.x);
  const top = Number(node.position.y);
  return document.nodes.some((other) => {
    if (other.nodeId === nodeId) return false;
    const otherSize = sizesById[other.nodeId] ?? AGENTFLOW_NODE_SIZE;
    const otherLeft = Number(other.position.x);
    const otherTop = Number(other.position.y);
    return (
      left < otherLeft + otherSize.width &&
      left + size.width > otherLeft &&
      top < otherTop + otherSize.height &&
      top + size.height > otherTop
    );
  });
}

export function applyNodePositions(
  document: AgentflowEditorDocument,
  positions: Record<string, AgentflowPosition>,
): AgentflowEditorDocument {
  return {
    ...document,
    nodes: document.nodes.map((node) => ({
      ...node,
      position: positions[node.nodeId] ?? node.position,
    })),
  };
}

export function searchNodes(document: AgentflowEditorDocument, query: string): AgentflowNode[] {
  const normalizedQuery = query.trim().toLocaleLowerCase();
  if (!normalizedQuery) return [];

  return document.nodes
    .filter((node) => {
      const values = [
        node.name ?? AGENTFLOW_NODE_LABELS[node.kind],
        AGENTFLOW_NODE_LABELS[node.kind],
        ...("members" in node && isBlockKind(node.kind)
          ? node.members.map((member) => member.name)
          : []),
      ];
      return values.some((value) => value.toLocaleLowerCase().includes(normalizedQuery));
    })
    .slice(0, 8);
}
