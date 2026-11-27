import assert from "node:assert/strict";
import test from "node:test";

import { routeAgentflowEdges, type EdgeRouteNode } from "./agentflow-edge-path";

const source: EdgeRouteNode = { id: "source", x: 0, y: 0, width: 240, height: 64 };
const target: EdgeRouteNode = { id: "target", x: 400, y: 0, width: 240, height: 64 };

function routed(
  nodes: EdgeRouteNode[],
  sourceNodeId: string,
  targetNodeId: string,
  edgeId = "edge",
) {
  return routeAgentflowEdges(nodes, [{ edgeId, sourceNodeId, targetNodeId }])[edgeId];
}

test("same-row neighbours get a straight line", () => {
  const route = routed([source, target], "source", "target");

  assert.equal(route.points.length, 2);
  assert.match(route.path, /^M 240 32 L 400 32$/);
});

test("an edge waits for both endpoint nodes to be available", () => {
  assert.deepEqual(
    routeAgentflowEdges(
      [source],
      [{ edgeId: "pending", sourceNodeId: "source", targetNodeId: "target" }],
    ),
    {},
  );
});

test("different rows get a two-segment route", () => {
  const lowerTarget = { ...target, y: 180 };
  const route = routed([source, lowerTarget], "source", "target");

  assert.equal(route.points.length, 4);
  assert.equal(route.points[1].x, route.points[2].x);
});

test("routes sharing a midpoint offset each source group by 14px", () => {
  const nodes = [
    source,
    { ...source, id: "lower-source", y: 300 },
    target,
    { ...target, id: "lower-target", y: 300 },
    { ...target, id: "bottom-target", y: 450 },
  ];
  const routes = routeAgentflowEdges(nodes, [
    { edgeId: "first", sourceNodeId: source.id, targetNodeId: "lower-target" },
    { edgeId: "second", sourceNodeId: "lower-source", targetNodeId: target.id },
    { edgeId: "same-source", sourceNodeId: source.id, targetNodeId: "bottom-target" },
  ]);

  assert.equal(routes.first.points[1].x, 320);
  assert.equal(routes.second.points[1].x, 334);
  assert.equal(routes["same-source"].points[1].x, 320);
  assert.equal(routes.second.label.x, 334);
});

test("a back edge on the same row passes below", () => {
  const route = routed([source, target], "target", "source");

  assert.ok(route.points.some((point) => point.y > 64));
});

test("a blocked forward edge passes above", () => {
  const blocker = { id: "blocker", x: 280, y: 0, width: 80, height: 64 };
  const route = routed([source, target, blocker], "source", "target");

  assert.ok(route.points.some((point) => point.y < 0));
});

test("a blocked forward edge stays near its row when another card is far above", () => {
  const blocker = { ...source, id: "commit", x: 640 };
  const output = { ...target, x: 960 };
  const upperCard = { id: "group-chat", x: 320, y: -740, width: 240, height: 158 };
  const localRoute = routed([source, output, blocker], "source", "target");
  const route = routed([source, output, blocker, upperCard], "source", "target");

  assert.deepEqual(route.points, localRoute.points);
});

test("a forward detour uses the shorter side when it can finish level with the target", () => {
  const lowerTarget = { ...target, y: 100 };
  const blocker = { id: "blocker", x: 280, y: -130, width: 80, height: 228 };
  const route = routed([source, lowerTarget, blocker], "source", "target");

  assert.equal(route.points[2].y, lowerTarget.y + lowerTarget.height / 2);
  assert.doesNotMatch(route.path, /NaN/);
});

test("edges sharing a corridor are offset", () => {
  const routes = routeAgentflowEdges(
    [source, target],
    [
      { edgeId: "edge-a", sourceNodeId: "target", targetNodeId: "source" },
      { edgeId: "edge-b", sourceNodeId: "target", targetNodeId: "source" },
    ],
  );

  const firstCorridor = routes["edge-a"].points[2].y;
  const secondCorridor = routes["edge-b"].points[2].y;
  assert.notEqual(firstCorridor, secondCorridor);
});

test("offsets keep shared detours outside a blocker", () => {
  const blocker = { id: "blocker", x: 280, y: 0, width: 80, height: 64 };
  const targets = [400, 720, 1040, 1360].map((x) => ({ ...target, id: `target-${x}`, x }));
  const routes = routeAgentflowEdges(
    [source, blocker, ...targets],
    targets.map(({ id }) => ({ edgeId: id, sourceNodeId: source.id, targetNodeId: id })),
  );

  for (const route of Object.values(routes)) {
    const channelY = route.points[2].y;
    assert.ok(channelY < blocker.y || channelY > blocker.y + blocker.height);
  }
});

test("no segment crosses a node other than its endpoints", () => {
  const nodes: EdgeRouteNode[] = [
    source,
    { ...target, y: 120 },
    { id: "blocker", x: 280, y: 20, width: 80, height: 64 },
  ];
  const route = routed(nodes, "source", "target");
  const blocker = nodes[2];

  for (let index = 1; index < route.points.length; index += 1) {
    const start = route.points[index - 1];
    const end = route.points[index];
    const crossesBlocker =
      (start.y === end.y &&
        start.y > blocker.y &&
        start.y < blocker.y + blocker.height &&
        Math.max(start.x, end.x) > blocker.x &&
        Math.min(start.x, end.x) < blocker.x + blocker.width) ||
      (start.x === end.x &&
        start.x > blocker.x &&
        start.x < blocker.x + blocker.width &&
        Math.max(start.y, end.y) > blocker.y &&
        Math.min(start.y, end.y) < blocker.y + blocker.height);
    assert.equal(crossesBlocker, false);
  }
});

test("label anchors sit outside every node", () => {
  const nodes = [source, target, { id: "blocker", x: 280, y: 0, width: 80, height: 64 }];
  const route = routed(nodes, "source", "target");

  assert.equal(
    nodes.some(
      (node) =>
        route.label.x >= node.x &&
        route.label.x <= node.x + node.width &&
        route.label.y >= node.y &&
        route.label.y <= node.y + node.height,
    ),
    false,
  );
});
