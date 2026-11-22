import type { ElkExtendedEdge, ElkNode } from "elkjs";
import ELK from "elkjs/lib/elk.bundled.js";
import type { AgentflowPosition } from "../../../../types/agentflow";
import {
  AGENTFLOW_NODE_SIZE,
  snapAgentflowPosition,
  type AgentflowNodeSize,
} from "./agentflow-canvas-geometry";
import { describeAgentflowEdge } from "./agentflow-condition";
import type { AgentflowEditorDocument } from "./agentflow-document";

const elk = new ELK();

export async function createGraphLayout(
  document: AgentflowEditorDocument,
  sizes: Record<string, AgentflowNodeSize>,
  canvas: { width: number; height: number },
): Promise<Record<string, AgentflowPosition>> {
  const nodes: ElkNode[] = document.nodes.map((node) => {
    const size = Object.hasOwn(sizes, node.nodeId) ? sizes[node.nodeId] : undefined;
    return {
      id: `node:${node.nodeId}`,
      width: size?.width ?? AGENTFLOW_NODE_SIZE.width,
      height: size?.height ?? AGENTFLOW_NODE_SIZE.height,
    };
  });
  const nodeIds = new Set(document.nodes.map((node) => node.nodeId));
  const edges: ElkExtendedEdge[] = document.edges
    .filter((edge) => nodeIds.has(edge.sourceNodeId) && nodeIds.has(edge.targetNodeId))
    .map((edge) => {
      const text = describeAgentflowEdge(edge, document.nodes, document.edges);
      return {
        id: `edge:${edge.edgeId}`,
        sources: [`node:${edge.sourceNodeId}`],
        targets: [`node:${edge.targetNodeId}`],
        ...(text
          ? {
              labels: [
                { id: `label:${edge.edgeId}`, text, width: text.length * 6.2 + 18, height: 22 },
              ],
            }
          : {}),
      };
    });
  const result = await elk.layout({
    id: "root",
    children: nodes,
    edges,
    layoutOptions: {
      "elk.algorithm": "layered",
      "elk.direction": "RIGHT",
      "elk.spacing.nodeNode": "40",
      "elk.layered.spacing.nodeNodeBetweenLayers": "80",
      "elk.layered.wrapping.strategy": "MULTI_EDGE",
      "elk.aspectRatio": String(canvas.width / canvas.height),
      "elk.layered.wrapping.additionalEdgeSpacing": "120",
    },
  });
  return Object.fromEntries(
    (result.children ?? []).map((node) => [
      node.id.slice("node:".length),
      snapAgentflowPosition({ x: node.x ?? 0, y: node.y ?? 0 }),
    ]),
  );
}
