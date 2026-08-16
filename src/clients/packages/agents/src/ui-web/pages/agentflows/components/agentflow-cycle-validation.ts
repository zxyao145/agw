import { AgentflowEdgeKind, AgentflowNodeKind } from "../../../../types/agentflow";

type CycleNode = {
  id: string;
  data: {
    kind: AgentflowNodeKind;
    title?: string;
  };
};

type CycleEdge = {
  source: string;
  target: string;
  data?: {
    kind?: AgentflowEdgeKind;
  };
};

export function validateAgentflowCycles(nodes: CycleNode[], edges: CycleEdge[]): string | null {
  const adjacency = new Map(nodes.map((node) => [node.id, [] as string[]]));
  for (const edge of edges) {
    adjacency.get(edge.source)?.push(edge.target);
  }

  let nextIndex = 0;
  const indexes = new Map<string, number>();
  const lowLinks = new Map<string, number>();
  const stack: string[] = [];
  const onStack = new Set<string>();
  const cyclicComponents: Set<string>[] = [];

  const visit = (nodeId: string) => {
    indexes.set(nodeId, nextIndex);
    lowLinks.set(nodeId, nextIndex);
    nextIndex += 1;
    stack.push(nodeId);
    onStack.add(nodeId);

    for (const nextNodeId of adjacency.get(nodeId) ?? []) {
      if (!indexes.has(nextNodeId)) {
        visit(nextNodeId);
        lowLinks.set(nodeId, Math.min(lowLinks.get(nodeId)!, lowLinks.get(nextNodeId)!));
      } else if (onStack.has(nextNodeId)) {
        lowLinks.set(nodeId, Math.min(lowLinks.get(nodeId)!, indexes.get(nextNodeId)!));
      }
    }

    if (lowLinks.get(nodeId) !== indexes.get(nodeId)) return;

    const component = new Set<string>();
    let currentNodeId: string;
    do {
      currentNodeId = stack.pop()!;
      onStack.delete(currentNodeId);
      component.add(currentNodeId);
    } while (currentNodeId !== nodeId);

    if (component.size > 1 || (adjacency.get(nodeId) ?? []).includes(nodeId)) {
      cyclicComponents.push(component);
    }
  };

  for (const node of nodes) {
    if (!indexes.has(node.id)) visit(node.id);
  }

  const nodeById = new Map(nodes.map((node) => [node.id, node]));
  for (const component of cyclicComponents) {
    const hasConditionalExit = edges.some(
      (edge) =>
        component.has(edge.source) &&
        !component.has(edge.target) &&
        (edge.data?.kind === AgentflowEdgeKind.SwitchCase ||
          edge.data?.kind === AgentflowEdgeKind.SwitchDefault),
    );
    if (!hasConditionalExit) {
      const names = [...component]
        .map((nodeId) => nodeById.get(nodeId)?.data.title || nodeId)
        .sort()
        .join(", ");
      return `Cycle containing ${names} needs an If / Else branch that exits the cycle`;
    }

    const hasUnsupportedOutsideBarrierSource = edges.some(
      (edge) =>
        edge.data?.kind === AgentflowEdgeKind.FanInBarrier &&
        component.has(edge.target) &&
        !component.has(edge.source) &&
        nodeById.get(edge.source)?.data.kind !== AgentflowNodeKind.Input,
    );
    if (hasUnsupportedOutsideBarrierSource) {
      return "A Fan-in Barrier entering a cycle can only reuse the Input node from outside the cycle";
    }
  }

  return null;
}
