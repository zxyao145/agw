import type { components } from "@agw/api";

type AgentflowDefinitionDto = components["schemas"]["AgentflowDefinitionDto"];
type AgentflowNodeDto = components["schemas"]["AgentflowNodeDto"];

/** 按执行方向排列顶层节点。Orders top-level nodes along the direction of execution. */
export function orderAgentflowNodes(definition: AgentflowDefinitionDto): AgentflowNodeDto[] {
  const { nodes, edges } = definition;
  const nodeIndices = new Map(nodes.map(({ nodeId }, index) => [nodeId, index]));
  const outgoing: number[][] = nodes.map(() => []);
  for (const edge of edges) {
    const source = nodeIndices.get(edge.sourceNodeId);
    const target = nodeIndices.get(edge.targetNodeId);
    if (source !== undefined && target !== undefined) outgoing[source].push(target);
  }

  const inputIndices = nodes.flatMap(({ kind }, index) => (kind === "input" ? [index] : []));
  const indices = nodes.map((_, index) => index);
  const { forwardEdges, finished } = findForwardEdges(outgoing, [...inputIndices, ...indices]);
  const layers = nodes.map(() => 0);
  for (const source of finished.reverse()) {
    for (const target of forwardEdges[source]) {
      layers[target] = Math.max(layers[target], layers[source] + 1);
    }
  }

  return indices
    .sort(
      (a, b) =>
        layers[a] - layers[b] ||
        Number(nodes[a].position.y) - Number(nodes[b].position.y) ||
        Number(nodes[a].position.x) - Number(nodes[b].position.x) ||
        a - b,
    )
    .map((index) => nodes[index]);
}

function findForwardEdges(outgoing: number[][], starts: number[]) {
  const visiting = new Set<number>();
  const visited = new Set<number>();
  const forwardEdges: number[][] = outgoing.map(() => []);
  const finished: number[] = [];

  for (const start of starts) {
    if (visited.has(start)) continue;
    const stack = [{ node: start, targets: outgoing[start].values() }];
    visiting.add(start);
    while (stack.length > 0) {
      const frame = stack[stack.length - 1];
      const next = frame.targets.next();
      if (next.done) {
        visiting.delete(frame.node);
        visited.add(frame.node);
        finished.push(frame.node);
        stack.pop();
        continue;
      }
      const target = next.value;
      if (visiting.has(target)) continue;
      forwardEdges[frame.node].push(target);
      if (visited.has(target)) continue;
      visiting.add(target);
      stack.push({ node: target, targets: outgoing[target].values() });
    }
  }

  return { forwardEdges, finished };
}
