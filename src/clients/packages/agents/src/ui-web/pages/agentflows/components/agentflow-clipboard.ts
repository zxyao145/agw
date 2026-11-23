import type { AgentflowEdge, AgentflowNode, AgentflowPosition } from "../../../../types/agentflow";
import { createId, type AgentflowEditorDocument } from "./agentflow-document";
import { renumberSwitchCases } from "./agentflow-edge-routing";

export type AgentflowClipboard = { nodes: AgentflowNode[]; edges: AgentflowEdge[] };

/** 复制选中的可编辑节点以及它们之间的连线。Copies selected editable nodes and the edges between them. */
export function copyNodes(
  document: AgentflowEditorDocument,
  nodeIds: string[],
): AgentflowClipboard | null {
  const selected = new Set(nodeIds);
  const nodes = document.nodes.filter((node) => node.kind !== "input" && selected.has(node.nodeId));
  if (nodes.length === 0) return null;
  const copiedIds = new Set(nodes.map((node) => node.nodeId));
  const edges = document.edges.filter(
    (edge) => copiedIds.has(edge.sourceNodeId) && copiedIds.has(edge.targetNodeId),
  );
  return structuredClone({ nodes, edges });
}

export function pasteNodes(
  document: AgentflowEditorDocument,
  clipboard: AgentflowClipboard,
  placement: { offset: number } | { position: AgentflowPosition },
): { document: AgentflowEditorDocument; nodeIds: string[] } {
  const pasted = structuredClone(clipboard);
  const offset =
    "offset" in placement
      ? { x: placement.offset, y: placement.offset }
      : {
          x:
            placement.position.x - Math.min(...pasted.nodes.map((node) => Number(node.position.x))),
          y:
            placement.position.y - Math.min(...pasted.nodes.map((node) => Number(node.position.y))),
        };
  const nodeIdByOriginal = new Map<string, string>();
  const choiceIdsByNode = new Map<string, Map<string, string>>();
  for (const node of pasted.nodes) {
    const originalId = node.nodeId;
    node.nodeId = createId(node.kind);
    nodeIdByOriginal.set(originalId, node.nodeId);
    node.position = {
      x: Number(node.position.x) + offset.x,
      y: Number(node.position.y) + offset.y,
    };
    if ("members" in node) {
      for (const member of node.members) member.memberId = createId("member");
    }
    if (node.kind === "humanGate") {
      const ids = new Map<string, string>();
      for (const choice of node.choices) {
        const choiceId = createId("choice");
        ids.set(choice.choiceId, choiceId);
        choice.choiceId = choiceId;
      }
      choiceIdsByNode.set(originalId, ids);
    }
  }
  const edges = pasted.edges.map((edge) => ({
    ...edge,
    edgeId: createId("edge"),
    sourceNodeId: nodeIdByOriginal.get(edge.sourceNodeId)!,
    targetNodeId: nodeIdByOriginal.get(edge.targetNodeId)!,
    choiceId: edge.choiceId
      ? (choiceIdsByNode.get(edge.sourceNodeId)?.get(edge.choiceId) ?? null)
      : null,
  }));
  const nodeIds = pasted.nodes.map((node) => node.nodeId);
  return {
    document: renumberSwitchCases(
      {
        ...document,
        nodes: [...document.nodes, ...pasted.nodes],
        edges: [...document.edges, ...edges],
      },
      nodeIds,
    ),
    nodeIds,
  };
}

export function deleteNodes(
  document: AgentflowEditorDocument,
  nodeIds: string[],
): AgentflowEditorDocument {
  const selected = new Set(nodeIds);
  const removed = new Set(
    document.nodes
      .filter((node) => node.kind !== "input" && selected.has(node.nodeId))
      .map((node) => node.nodeId),
  );
  if (removed.size === 0) return document;
  const affectedSources = new Set<string>();
  const edges = document.edges.filter((edge) => {
    if (removed.has(edge.sourceNodeId)) return false;
    if (!removed.has(edge.targetNodeId)) return true;
    affectedSources.add(edge.sourceNodeId);
    return false;
  });
  return renumberSwitchCases(
    {
      ...document,
      nodes: document.nodes.filter((node) => !removed.has(node.nodeId)),
      edges,
    },
    [...affectedSources],
  );
}

export function deleteEdge(
  document: AgentflowEditorDocument,
  edgeId: string,
): AgentflowEditorDocument {
  const edge = document.edges.find((candidate) => candidate.edgeId === edgeId);
  return edge
    ? renumberSwitchCases(
        {
          ...document,
          edges: document.edges.filter((candidate) => candidate.edgeId !== edgeId),
        },
        [edge.sourceNodeId],
      )
    : document;
}
