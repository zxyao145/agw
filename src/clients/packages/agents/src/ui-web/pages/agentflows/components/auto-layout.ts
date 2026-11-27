import type { ElkExtendedEdge, ElkNode } from "elkjs";
import ELK from "elkjs/lib/elk.bundled.js";
import type { AgentflowEdge, AgentflowPosition } from "../../../../types/agentflow";
import {
  AGENTFLOW_NODE_SIZE,
  snapAgentflowPosition,
  type AgentflowNodeSize,
} from "./agentflow-canvas-geometry";
import { describeAgentflowEdge } from "./agentflow-condition";
import type { AgentflowEditorDocument } from "./agentflow-document";

const elk = new ELK();
const NODE_SPACING = 60;

type NodeBounds = AgentflowPosition & AgentflowNodeSize;
type LayoutNode = NodeBounds & { id: string };

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
  const validEdges = document.edges.filter(
    (edge) => nodeIds.has(edge.sourceNodeId) && nodeIds.has(edge.targetNodeId),
  );
  const successors = new Map<string, Set<string>>();
  const incomingCounts = new Map<string, number>();
  for (const { sourceNodeId, targetNodeId } of validEdges) {
    let targets = successors.get(sourceNodeId);
    if (!targets) {
      targets = new Set();
      successors.set(sourceNodeId, targets);
    }
    targets.add(targetNodeId);
    incomingCounts.set(targetNodeId, (incomingCounts.get(targetNodeId) ?? 0) + 1);
  }
  // 逐条排除有替代路径的旁路，保留回路中的连线。
  // Exclude bypasses one at a time to preserve reachability and keep cycle edges.
  for (const { sourceNodeId: source, targetNodeId: target } of validEdges) {
    const targets = successors.get(source)!;
    if (source === target || targets.size < 2 || incomingCounts.get(target)! < 2) continue;
    if (!targets.delete(target)) continue;
    if (!canReach(successors, source, target) || canReach(successors, target, source)) {
      targets.add(target);
    }
  }
  const edges: ElkExtendedEdge[] = validEdges.map((edge) => {
    const text = describeAgentflowEdge(edge, document.nodes, document.edges);
    return {
      id: `edge:${edge.edgeId}`,
      sources: [`node:${edge.sourceNodeId}`],
      targets: [`node:${edge.targetNodeId}`],
      ...(!successors.get(edge.sourceNodeId)!.has(edge.targetNodeId)
        ? { layoutOptions: { "elk.noLayout": "true" } }
        : {}),
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
      "elk.spacing.nodeNode": String(NODE_SPACING),
      "elk.layered.spacing.nodeNodeBetweenLayers": "80",
      "elk.layered.wrapping.strategy": "OFF",
      "elk.aspectRatio": String(canvas.width / canvas.height),
    },
  });
  const placedNodes: LayoutNode[] = (result.children ?? []).map((node) => ({
    id: node.id.slice("node:".length),
    ...snapAgentflowPosition({ x: node.x ?? 0, y: node.y ?? 0 }),
    width: node.width ?? AGENTFLOW_NODE_SIZE.width,
    height: node.height ?? AGENTFLOW_NODE_SIZE.height,
  }));
  const inputId = document.nodes.find((node) => node.kind === "input")?.nodeId;
  if (inputId !== undefined) alignLongestPath(placedNodes, validEdges, inputId);
  return Object.fromEntries(placedNodes.map(({ id, x, y }) => [id, { x, y }]));
}

function alignLongestPath(nodes: LayoutNode[], edges: AgentflowEdge[], inputId: string): void {
  const byId = new Map(nodes.map((node) => [node.id, node]));
  const next = new Map<string, { id: string; length: number }>();
  // 从右侧开始计算路径长度，回边留在布线阶段。
  // Compute path lengths from the right, leaving back edges to the router.
  const orderedEdges = [...edges].sort(
    (left, right) => byId.get(right.sourceNodeId)!.x - byId.get(left.sourceNodeId)!.x,
  );
  for (const { sourceNodeId, targetNodeId } of orderedEdges) {
    if (byId.get(sourceNodeId)!.x >= byId.get(targetNodeId)!.x) continue;
    const length = (next.get(targetNodeId)?.length ?? 0) + 1;
    if (length > (next.get(sourceNodeId)?.length ?? 0)) {
      next.set(sourceNodeId, { id: targetNodeId, length });
    }
  }
  if (!next.has(inputId)) return;

  const path: LayoutNode[] = [];
  for (let id: string | undefined = inputId; id !== undefined; id = next.get(id)?.id) {
    path.push(byId.get(id)!);
  }
  const centerY = Math.max(...path.map((node) => node.y + node.height / 2));
  const columnOffsets = new Map<number, number>();
  const pathIds = new Set(path.map((node) => node.id));
  for (const node of path) {
    const y = centerY - node.height / 2;
    columnOffsets.set(node.x, y - node.y);
    node.y = y;
  }
  const firstPortX = path[0].x + path[0].width;
  // 为主线跨越的列间留出空间。
  // Keep the horizontal segments between main-path nodes clear.
  const occupied: NodeBounds[] = [
    ...path,
    { x: firstPortX, y: centerY, width: path.at(-1)!.x - firstPortX, height: 0 },
  ];
  for (const node of [...nodes].sort((left, right) => left.y - right.y)) {
    if (pathIds.has(node.id)) continue;
    const preferredY = node.y + (columnOffsets.get(node.x) ?? 0);
    node.y = nearestFreeY(node, preferredY, occupied);
    occupied.push(node);
  }
}

function nearestFreeY(node: LayoutNode, preferredY: number, occupied: NodeBounds[]): number {
  const neighbors = occupied
    .filter((other) => other.x < node.x + node.width && other.x + other.width > node.x)
    .sort((left, right) => left.y - right.y);
  let gapTop = -Infinity;
  let closestY = Infinity;
  for (const other of neighbors) {
    const gapBottom = other.y - NODE_SPACING - node.height;
    if (gapTop <= gapBottom) {
      const y = Math.max(gapTop, Math.min(preferredY, gapBottom));
      if (Math.abs(y - preferredY) < Math.abs(closestY - preferredY)) closestY = y;
    }
    gapTop = Math.max(gapTop, other.y + other.height + NODE_SPACING);
  }
  const below = Math.max(gapTop, preferredY);
  return Math.abs(below - preferredY) < Math.abs(closestY - preferredY) ? below : closestY;
}

function canReach(successors: Map<string, Set<string>>, source: string, target: string): boolean {
  const seen = new Set([source]);
  const pending = [source];
  for (let index = 0; index < pending.length; index += 1) {
    for (const next of successors.get(pending[index]) ?? []) {
      if (next === target) return true;
      if (seen.has(next)) continue;
      seen.add(next);
      pending.push(next);
    }
  }
  return false;
}
