import type { AgentflowEdge, AgentflowPosition } from "../../../../types/agentflow";

export type EdgeRouteNode = { id: string; x: number; y: number; width: number; height: number };
export type EdgeRoute = {
  points: AgentflowPosition[];
  label: AgentflowPosition;
  path: string;
};

type EdgeToRoute = Pick<AgentflowEdge, "edgeId" | "sourceNodeId" | "targetNodeId">;
type CandidateRoute = EdgeRoute & {
  baseChannelY: number | null;
  channelPointIndices: [number, number] | null;
};
type RoutedEdge = CandidateRoute & EdgeToRoute;

const DETOUR_CLEARANCE = 34;

export function routeAgentflowEdges(
  nodes: EdgeRouteNode[],
  edges: EdgeToRoute[],
): Record<string, EdgeRoute> {
  const nodeById = new Map(nodes.map((node) => [node.id, node]));
  const candidates = edges.flatMap(({ edgeId, sourceNodeId, targetNodeId }) => {
    const source = nodeById.get(sourceNodeId);
    const target = nodeById.get(targetNodeId);
    if (!source || !target) return [];
    return [{ ...createRoute(nodes, source, target), edgeId, sourceNodeId, targetNodeId }];
  });

  offsetSharedCorridors(candidates, nodes);
  offsetSharedMiddleSegments(candidates);

  return Object.fromEntries(
    candidates.map(({ edgeId, points, label, path }) => [edgeId, { points, label, path }]),
  );
}

function createRoute(
  nodes: EdgeRouteNode[],
  source: EdgeRouteNode,
  target: EdgeRouteNode,
): CandidateRoute {
  const start = { x: source.x + source.width, y: source.y + source.height / 2 };
  const end = { x: target.x, y: target.y + target.height / 2 };
  const forward = target.x >= source.x;

  if (
    end.x - start.x >= 28 &&
    start.y === end.y &&
    routeIsClear([start, end], nodes, source.id, target.id)
  ) {
    return candidate([start, end], { x: (start.x + end.x) / 2, y: start.y }, null, null);
  }

  if (end.x - start.x >= 28) {
    const middleX = (start.x + end.x) / 2;
    const points = [start, { x: middleX, y: start.y }, { x: middleX, y: end.y }, end];
    if (routeIsClear(points, nodes, source.id, target.id)) {
      const verticalDistance = Math.abs(end.y - start.y);
      const labelY =
        verticalDistance > 120 ? start.y + Math.sign(end.y - start.y) * 56 : (start.y + end.y) / 2;
      return candidate(points, { x: middleX, y: labelY }, null, [1, 2]);
    }
  }

  const sourceStub = { x: start.x + 24, y: start.y };
  const targetStub = { x: end.x - 24, y: end.y };
  const separatedBelow = target.y >= source.y + source.height + 40;
  const separatedAbove = source.y >= target.y + target.height + 40;
  const corridorNodes = nodes.filter(
    (node) =>
      node.id !== source.id &&
      node.id !== target.id &&
      node.x < Math.max(sourceStub.x, targetStub.x) &&
      node.x + node.width > Math.min(sourceStub.x, targetStub.x),
  );

  const pointsVia = (y: number) => [
    start,
    sourceStub,
    { x: sourceStub.x, y },
    { x: targetStub.x, y },
    targetStub,
    end,
  ];
  const canUseChannel = (y: number) => routeIsClear(pointsVia(y), nodes, source.id, target.id);
  let channelY = separatedBelow
    ? (source.y + source.height + target.y) / 2
    : separatedAbove
      ? (source.y + target.y + target.height) / 2
      : undefined;

  if (channelY === undefined || !canUseChannel(channelY)) {
    const channels =
      sourceStub.x < targetStub.x
        ? corridorNodes.flatMap(({ y, height }) => [
            y - DETOUR_CLEARANCE,
            y + height + DETOUR_CLEARANCE,
          ])
        : [];
    const verticalLength = (y: number) => Math.abs(start.y - y) + Math.abs(end.y - y);
    channelY =
      [...new Set(channels)]
        .sort((left, right) => verticalLength(left) - verticalLength(right) || left - right)
        .find(canUseChannel) ?? fallbackChannelY(corridorNodes, source, target, forward);
  }

  const channelWidth = Math.abs(targetStub.x - sourceStub.x);
  const labelX =
    channelWidth < 192
      ? (sourceStub.x + targetStub.x) / 2
      : sourceStub.x + Math.sign(targetStub.x - sourceStub.x) * 96;
  const label = findOpenLabel(
    { x: labelX, y: channelY },
    sourceStub,
    targetStub,
    channelY,
    nodes,
    source.id,
    target.id,
  );
  return candidate(pointsVia(channelY), label, channelY, [2, 3]);
}

function fallbackChannelY(
  corridorNodes: EdgeRouteNode[],
  source: EdgeRouteNode,
  target: EdgeRouteNode,
  forward: boolean,
): number {
  if (forward) {
    const top = Math.min(source.y, target.y, ...corridorNodes.map((node) => node.y));
    return top - DETOUR_CLEARANCE;
  }

  const bottom = Math.max(
    source.y + source.height,
    target.y + target.height,
    ...corridorNodes.map((node) => node.y + node.height),
  );
  return bottom + DETOUR_CLEARANCE;
}

function candidate(
  points: EdgeRoute["points"],
  label: EdgeRoute["label"],
  baseChannelY: number | null,
  channelPointIndices: [number, number] | null,
): CandidateRoute {
  return {
    points,
    label,
    path: createRoundedPath(points),
    baseChannelY,
    channelPointIndices,
  };
}

function offsetSharedCorridors(routes: RoutedEdge[], nodes: EdgeRouteNode[]): void {
  const groups: { baseY: number; count: number }[] = [];
  for (const route of routes) {
    const baseY = route.baseChannelY;
    if (baseY === null || !route.channelPointIndices) continue;
    let group = groups.find((item) => Math.abs(item.baseY - baseY) < 24);
    if (!group) {
      group = { baseY, count: 0 };
      groups.push(group);
    }
    const offset = group.count * 12;
    group.count += 1;
    if (!offset) continue;
    const [firstIndex, secondIndex] = route.channelPointIndices;
    const y = baseY + offset;
    const points = [...route.points];
    points[firstIndex] = { ...points[firstIndex], y };
    points[secondIndex] = { ...points[secondIndex], y };
    if (!routeIsClear(points, nodes, route.sourceNodeId, route.targetNodeId)) continue;
    route.points = points;
    route.label = { ...route.label, y };
    route.path = createRoundedPath(points);
  }
}

function offsetSharedMiddleSegments(routes: RoutedEdge[]): void {
  const offsetsByMidpoint = new Map<number, Map<string, number>>();
  for (const route of routes) {
    if (route.baseChannelY !== null || !route.channelPointIndices || route.points.length !== 4)
      continue;
    const baseX = route.points[1].x;
    let sourceOffsets = offsetsByMidpoint.get(baseX);
    if (!sourceOffsets) {
      sourceOffsets = new Map();
      offsetsByMidpoint.set(baseX, sourceOffsets);
    }
    const offset = sourceOffsets.get(route.sourceNodeId) ?? sourceOffsets.size * 14;
    sourceOffsets.set(route.sourceNodeId, offset);
    if (offset === 0) continue;
    const middleX = baseX + offset;
    route.points[1] = { ...route.points[1], x: middleX };
    route.points[2] = { ...route.points[2], x: middleX };
    route.label = { ...route.label, x: middleX };
    route.path = createRoundedPath(route.points);
  }
}

function routeIsClear(
  points: EdgeRoute["points"],
  nodes: EdgeRouteNode[],
  sourceNodeId: string,
  targetNodeId: string,
): boolean {
  return nodes
    .filter((node) => node.id !== sourceNodeId && node.id !== targetNodeId)
    .every((node) =>
      points.slice(1).every((end, index) => !segmentCrossesNode(points[index], end, node)),
    );
}

function segmentCrossesNode(
  start: EdgeRoute["points"][number],
  end: EdgeRoute["points"][number],
  node: EdgeRouteNode,
): boolean {
  const right = node.x + node.width;
  const bottom = node.y + node.height;
  if (start.y === end.y) {
    return (
      start.y > node.y &&
      start.y < bottom &&
      Math.max(start.x, end.x) > node.x &&
      Math.min(start.x, end.x) < right
    );
  }
  if (start.x === end.x) {
    return (
      start.x > node.x &&
      start.x < right &&
      Math.max(start.y, end.y) > node.y &&
      Math.min(start.y, end.y) < bottom
    );
  }
  return false;
}

function findOpenLabel(
  initial: EdgeRoute["label"],
  sourceStub: EdgeRoute["points"][number],
  targetStub: EdgeRoute["points"][number],
  channelY: number,
  nodes: EdgeRouteNode[],
  sourceNodeId: string,
  targetNodeId: string,
): EdgeRoute["label"] {
  const direction = Math.sign(targetStub.x - sourceStub.x);
  for (let index = 0; index < 20; index += 1) {
    const label = { x: initial.x + direction * index * 12, y: channelY };
    if (
      label.x >= Math.min(sourceStub.x, targetStub.x) &&
      label.x <= Math.max(sourceStub.x, targetStub.x) &&
      !nodes.some(
        (node) =>
          node.id !== sourceNodeId &&
          node.id !== targetNodeId &&
          label.x >= node.x - 100 &&
          label.x <= node.x + node.width + 100 &&
          label.y >= node.y - 11 &&
          label.y <= node.y + node.height + 11,
      )
    ) {
      return label;
    }
  }
  return initial;
}

function createRoundedPath(points: EdgeRoute["points"]): string {
  if (points.length < 3) return `M ${formatPoint(points[0])} L ${formatPoint(points.at(-1)!)}`;

  const commands = [`M ${formatPoint(points[0])}`];
  for (let index = 1; index < points.length - 1; index += 1) {
    const previous = points[index - 1];
    const corner = points[index];
    const next = points[index + 1];
    const radius = Math.min(10, distance(previous, corner) / 2, distance(corner, next) / 2);
    const before = pointToward(corner, previous, radius);
    const after = pointToward(corner, next, radius);
    commands.push(`L ${formatPoint(before)} Q ${formatPoint(corner)} ${formatPoint(after)}`);
  }
  commands.push(`L ${formatPoint(points.at(-1)!)}`);
  return commands.join(" ");
}

function pointToward(
  origin: EdgeRoute["points"][number],
  target: EdgeRoute["points"][number],
  distanceToMove: number,
) {
  if (distanceToMove === 0) return origin;
  const totalDistance = distance(origin, target);
  return {
    x: origin.x + ((target.x - origin.x) / totalDistance) * distanceToMove,
    y: origin.y + ((target.y - origin.y) / totalDistance) * distanceToMove,
  };
}

function distance(left: EdgeRoute["points"][number], right: EdgeRoute["points"][number]): number {
  return Math.hypot(right.x - left.x, right.y - left.y);
}

function formatPoint(point: EdgeRoute["points"][number]): string {
  return `${formatNumber(point.x)} ${formatNumber(point.y)}`;
}

function formatNumber(value: number): string {
  return Number.isInteger(value) ? `${value}` : value.toFixed(1);
}
