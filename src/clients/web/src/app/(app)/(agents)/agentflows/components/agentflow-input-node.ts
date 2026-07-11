import type { Edge, Node } from "reactflow";

export const INPUT_NODE_ID = "input";

type AgentflowInputNodeData = {
  kind: number;
  title: string;
  relateId: string | null;
  instructions: string;
  configJson: string;
};

type AgentflowInputEdgeData = {
  kind: number;
  label: string;
  conditionJson: string;
  configJson: string;
};

const NodeKind = {
  Agent: 0,
  WorkflowAsAgent: 1,
  ConcurrentBlock: 5,
  HandoffBlock: 6,
  GroupChatBlock: 7,
  MagenticBlock: 8,
  Input: 10,
} as const;

const EdgeKind = {
  FanOut: 1,
} as const;

export function createInputNode<
  TNodeData extends AgentflowInputNodeData = AgentflowInputNodeData,
>(): Node<TNodeData> {
  return {
    id: INPUT_NODE_ID,
    type: "dagNode",
    position: { x: 0, y: 120 },
    deletable: false,
    data: {
      kind: NodeKind.Input,
      title: "Input",
      relateId: null,
      instructions: "",
      configJson: "",
    } as TNodeData,
  };
}

export function ensureInputGraph<
  TNodeData extends AgentflowInputNodeData,
  TEdgeData extends AgentflowInputEdgeData,
>(
  nodes: Node<TNodeData>[],
  edges: Edge<TEdgeData>[],
): { nodes: Node<TNodeData>[]; edges: Edge<TEdgeData>[] } {
  const existingInput = nodes.find(isInputNode);
  const inputNode = existingInput
    ? normalizeInputNode(existingInput)
    : createInputNode<TNodeData>();
  const nonInputNodes = nodes.filter((node) => !isInputNode(node));
  const normalizedNodes = [inputNode, ...nonInputNodes];

  if (existingInput) {
    return { nodes: normalizedNodes, edges };
  }

  const existingInputTargets = new Set(
    edges.filter((edge) => edge.source === INPUT_NODE_ID).map((edge) => edge.target),
  );
  const rootNodeIds = getRuntimeRootNodeIds(normalizedNodes, edges);
  const inputEdges = rootNodeIds
    .filter((rootNodeId) => !existingInputTargets.has(rootNodeId))
    .map((rootNodeId) => createInputEdge<TEdgeData>(rootNodeId));

  return { nodes: normalizedNodes, edges: [...inputEdges, ...edges] };
}

export function validateInputGraph<
  TNodeData extends { kind: number; configJson?: string | null; title?: string },
  TEdgeData extends { kind: number },
>(nodes: Node<TNodeData>[], edges: Edge<TEdgeData>[]) {
  const inputNodes = nodes.filter(isInputNode);
  if (inputNodes.length !== 1) {
    return { ok: false, message: "Agentflow needs exactly one Input node" };
  }

  const inputNode = inputNodes[0];
  if (inputNode.id !== INPUT_NODE_ID || inputNode.data.kind !== NodeKind.Input) {
    return { ok: false, message: "Input node must use the fixed input id" };
  }

  if (edges.some((edge) => edge.target === INPUT_NODE_ID)) {
    return { ok: false, message: "Input cannot have incoming edges" };
  }

  if (
    edges.some(
      (edge) =>
        edge.source === INPUT_NODE_ID && (edge.data?.kind ?? EdgeKind.FanOut) !== EdgeKind.FanOut,
    )
  ) {
    return { ok: false, message: "Input can only use Fan Out edges" };
  }

  const visibleNodeIds = getRuntimeVisibleNodeIds(nodes, edges);
  const reachableNodeIds = getReachableNodeIds(INPUT_NODE_ID, edges, visibleNodeIds);
  const unreachableNode = nodes.find(
    (node) =>
      visibleNodeIds.has(node.id) &&
      node.id !== INPUT_NODE_ID &&
      !reachableNodeIds.has(node.id),
  );
  if (unreachableNode) {
    return {
      ok: false,
      message: `${unreachableNode.data.title || unreachableNode.id} must start from Input`,
    };
  }

  return { ok: true, message: "Input rooted graph" };
}

export function isInputNode<TNodeData extends { kind: number }>(node: Node<TNodeData>) {
  return node.id === INPUT_NODE_ID || node.data.kind === NodeKind.Input;
}

function normalizeInputNode<TNodeData extends AgentflowInputNodeData>(
  node: Node<TNodeData>,
): Node<TNodeData> {
  return {
    ...node,
    id: INPUT_NODE_ID,
    deletable: false,
    data: {
      ...node.data,
      kind: NodeKind.Input,
      title: "Input",
      relateId: null,
      instructions: "",
      configJson: "",
    },
  };
}

function createInputEdge<TEdgeData extends AgentflowInputEdgeData>(
  targetNodeId: string,
): Edge<TEdgeData> {
  return {
    id: `edge-${INPUT_NODE_ID}-${targetNodeId}`,
    source: INPUT_NODE_ID,
    target: targetNodeId,
    data: {
      kind: EdgeKind.FanOut,
      label: "",
      conditionJson: "",
      configJson: "",
    } as TEdgeData,
  };
}

function getRuntimeRootNodeIds<TNodeData extends { kind: number; configJson?: string | null }>(
  nodes: Node<TNodeData>[],
  edges: Edge[],
) {
  const visibleNodeIds = getRuntimeVisibleNodeIds(nodes, edges);
  const visibleTargetIds = new Set(
    edges
      .filter((edge) => visibleNodeIds.has(edge.source) && visibleNodeIds.has(edge.target))
      .map((edge) => edge.target),
  );

  return nodes
    .filter(
      (node) =>
        node.id !== INPUT_NODE_ID &&
        visibleNodeIds.has(node.id) &&
        !visibleTargetIds.has(node.id),
    )
    .map((node) => node.id);
}

function getRuntimeVisibleNodeIds<
  TNodeData extends { kind: number; configJson?: string | null },
>(nodes: Node<TNodeData>[], edges: Edge[]) {
  const hiddenParticipantIds = getHiddenBlockParticipantIds(nodes, edges);
  return new Set(nodes.filter((node) => !hiddenParticipantIds.has(node.id)).map((node) => node.id));
}

function getReachableNodeIds(startNodeId: string, edges: Edge[], visibleNodeIds: Set<string>) {
  const adjacency = new Map<string, string[]>();
  visibleNodeIds.forEach((nodeId) => adjacency.set(nodeId, []));
  edges.forEach((edge) => {
    if (visibleNodeIds.has(edge.source) && visibleNodeIds.has(edge.target)) {
      adjacency.get(edge.source)?.push(edge.target);
    }
  });

  const reachableNodeIds = new Set<string>();
  const queue = [startNodeId];
  while (queue.length > 0) {
    const nodeId = queue.shift()!;
    if (reachableNodeIds.has(nodeId)) continue;
    reachableNodeIds.add(nodeId);
    queue.push(...(adjacency.get(nodeId) ?? []));
  }

  return reachableNodeIds;
}

function getHiddenBlockParticipantIds<
  TNodeData extends { kind: number; configJson?: string | null },
>(
  nodes: Node<TNodeData>[],
  edges: Edge[],
) {
  const nodeById = new Map(nodes.map((node) => [node.id, node]));
  const edgeNodeIds = new Set<string>();
  edges.forEach((edge) => {
    edgeNodeIds.add(edge.source);
    edgeNodeIds.add(edge.target);
  });

  const participantOwnersByNodeId = new Map<string, string[]>();
  nodes.forEach((node) => {
    if (!isBlockNodeKind(node.data.kind)) return;

    const config = readConfigJson(node.data.configJson || "") ?? {};
    uniqueStrings(readStringArray(config.participantNodeIds)).forEach((participantNodeId) => {
      const owners = participantOwnersByNodeId.get(participantNodeId) ?? [];
      owners.push(node.id);
      participantOwnersByNodeId.set(participantNodeId, owners);
    });
  });

  const hiddenParticipantIds = new Set<string>();
  participantOwnersByNodeId.forEach((ownerBlockIds, participantNodeId) => {
    const participantNode = nodeById.get(participantNodeId);
    if (
      participantNode &&
      isAgentParticipantKind(participantNode.data.kind) &&
      ownerBlockIds.length === 1 &&
      !edgeNodeIds.has(participantNodeId)
    ) {
      hiddenParticipantIds.add(participantNodeId);
    }
  });

  return hiddenParticipantIds;
}

function isAgentParticipantKind(kind: number) {
  return kind === NodeKind.Agent || kind === NodeKind.WorkflowAsAgent;
}

function isBlockNodeKind(kind: number) {
  return (
    kind === NodeKind.ConcurrentBlock ||
    kind === NodeKind.HandoffBlock ||
    kind === NodeKind.GroupChatBlock ||
    kind === NodeKind.MagenticBlock
  );
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

function readStringArray(value: unknown) {
  return Array.isArray(value)
    ? value.filter((item): item is string => typeof item === "string")
    : [];
}

function uniqueStrings(values: string[]) {
  return Array.from(new Set(values.filter((value) => value.trim().length > 0)));
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
