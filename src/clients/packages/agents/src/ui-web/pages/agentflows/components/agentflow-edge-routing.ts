import type { Edge } from "reactflow";

import { AgentflowEdgeKind } from "../../../../types/agentflow";

export type AgentflowEdgeData = {
  kind: AgentflowEdgeKind;
  label: string;
  conditionJson: string;
  configJson: string;
};

type RoutingStrategy = "direct" | "fan-out" | "switch";

const SWITCH_CASE_ORDER_KEY = "switchCaseOrder";

export function createDefaultEdgeData(
  kind: AgentflowEdgeKind = AgentflowEdgeKind.Direct,
): AgentflowEdgeData {
  return {
    kind,
    label: "",
    conditionJson: "",
    configJson: "",
  };
}

export function getDefaultEdgeKindForSource(
  edges: Edge<AgentflowEdgeData>[],
  sourceId: string,
  fallback: AgentflowEdgeKind,
) {
  const strategyEdge = edges.find(
    (edge) => edge.source === sourceId && edge.data?.kind !== AgentflowEdgeKind.FanInBarrier,
  );
  const strategy = strategyEdge ? getRoutingStrategy(strategyEdge.data?.kind) : null;

  if (strategy === "fan-out") return AgentflowEdgeKind.FanOut;
  if (strategy === "switch") return AgentflowEdgeKind.SwitchCase;
  if (strategy === "direct") return AgentflowEdgeKind.Direct;
  return fallback;
}

export function isPredicateEdgeKind(kind: AgentflowEdgeKind) {
  return (
    kind === AgentflowEdgeKind.Direct ||
    kind === AgentflowEdgeKind.FanOut ||
    kind === AgentflowEdgeKind.SwitchCase
  );
}

export function getSwitchCaseOrder(configJson: string) {
  const config = readConfigJson(configJson);
  const value = config?.[SWITCH_CASE_ORDER_KEY];
  return typeof value === "number" && Number.isInteger(value) && value >= 0 ? value : null;
}

export function setSwitchCaseOrder(configJson: string, order: number | null) {
  const config = readConfigJson(configJson);
  if (config === null) {
    return order === null
      ? configJson
      : JSON.stringify({ [SWITCH_CASE_ORDER_KEY]: order }, null, 2);
  }

  if (order === null) {
    if (!Object.hasOwn(config, SWITCH_CASE_ORDER_KEY)) return configJson;
    delete config[SWITCH_CASE_ORDER_KEY];
  } else {
    config[SWITCH_CASE_ORDER_KEY] = order;
  }

  return Object.keys(config).length > 0 ? JSON.stringify(config, null, 2) : "";
}

export function getNextSwitchCaseOrder(edges: Edge<AgentflowEdgeData>[], sourceId: string) {
  const orders = edges
    .filter((edge) => edge.source === sourceId && edge.data?.kind === AgentflowEdgeKind.SwitchCase)
    .map((edge) => getSwitchCaseOrder(edge.data?.configJson ?? ""))
    .filter((order): order is number => order !== null);
  return orders.length > 0 ? Math.max(...orders) + 1 : 0;
}

export function normalizeSwitchCaseOrders(edges: Edge<AgentflowEdgeData>[], sourceId?: string) {
  const sourceIds = sourceId
    ? [sourceId]
    : [
        ...new Set(
          edges
            .filter((edge) => edge.data?.kind === AgentflowEdgeKind.SwitchCase)
            .map((edge) => edge.source),
        ),
      ];
  const orderByEdgeId = new Map<string, number>();

  sourceIds.forEach((currentSourceId) => {
    edges
      .map((edge, index) => ({ edge, index }))
      .filter(
        ({ edge }) =>
          edge.source === currentSourceId && edge.data?.kind === AgentflowEdgeKind.SwitchCase,
      )
      .sort((left, right) => {
        const leftOrder = getSwitchCaseOrder(left.edge.data?.configJson ?? "");
        const rightOrder = getSwitchCaseOrder(right.edge.data?.configJson ?? "");
        return (
          (leftOrder ?? Number.MAX_SAFE_INTEGER) - (rightOrder ?? Number.MAX_SAFE_INTEGER) ||
          left.index - right.index
        );
      })
      .forEach(({ edge }, index) => orderByEdgeId.set(edge.id, index));
  });

  return edges.map((edge) => {
    const order = orderByEdgeId.get(edge.id);
    const data = { ...createDefaultEdgeData(), ...edge.data };
    if (order === undefined) {
      if (data.kind === AgentflowEdgeKind.SwitchCase) return edge;
      const configJson = setSwitchCaseOrder(data.configJson, null);
      return configJson === data.configJson ? edge : { ...edge, data: { ...data, configJson } };
    }

    return {
      ...edge,
      data: {
        ...data,
        configJson: setSwitchCaseOrder(data.configJson, order),
      },
    };
  });
}

export function moveSwitchCaseEdge(
  edges: Edge<AgentflowEdgeData>[],
  edgeId: string,
  direction: -1 | 1,
) {
  const edge = edges.find((candidate) => candidate.id === edgeId);
  if (!edge || edge.data?.kind !== AgentflowEdgeKind.SwitchCase) return edges;

  const normalized = normalizeSwitchCaseOrders(edges, edge.source);
  const cases = normalized
    .filter(
      (candidate) =>
        candidate.source === edge.source && candidate.data?.kind === AgentflowEdgeKind.SwitchCase,
    )
    .sort(
      (left, right) =>
        (getSwitchCaseOrder(left.data?.configJson ?? "") ?? 0) -
        (getSwitchCaseOrder(right.data?.configJson ?? "") ?? 0),
    );
  const currentIndex = cases.findIndex((candidate) => candidate.id === edgeId);
  const nextIndex = currentIndex + direction;
  if (currentIndex < 0 || nextIndex < 0 || nextIndex >= cases.length) return normalized;

  [cases[currentIndex], cases[nextIndex]] = [cases[nextIndex], cases[currentIndex]];
  const orderByEdgeId = new Map(cases.map((candidate, index) => [candidate.id, index]));
  return normalized.map((candidate) => {
    const order = orderByEdgeId.get(candidate.id);
    if (order === undefined) return candidate;

    const data = { ...createDefaultEdgeData(), ...candidate.data };
    return {
      ...candidate,
      data: {
        ...data,
        configJson: setSwitchCaseOrder(data.configJson, order),
      },
    };
  });
}

export function removeAgentflowEdge(edges: Edge<AgentflowEdgeData>[], edgeId: string) {
  const sourceId = edges.find((edge) => edge.id === edgeId)?.source;
  return normalizeSwitchCaseOrders(
    edges.filter((edge) => edge.id !== edgeId),
    sourceId,
  );
}

export function getSwitchCasePosition(edges: Edge<AgentflowEdgeData>[], edgeId: string) {
  const edge = edges.find((candidate) => candidate.id === edgeId);
  if (!edge || edge.data?.kind !== AgentflowEdgeKind.SwitchCase) {
    return null;
  }

  const cases = edges
    .filter(
      (candidate) =>
        candidate.source === edge.source && candidate.data?.kind === AgentflowEdgeKind.SwitchCase,
    )
    .sort((left, right) => {
      const leftOrder = getSwitchCaseOrder(left.data?.configJson ?? "");
      const rightOrder = getSwitchCaseOrder(right.data?.configJson ?? "");
      return (leftOrder ?? Number.MAX_SAFE_INTEGER) - (rightOrder ?? Number.MAX_SAFE_INTEGER);
    });
  const index = cases.findIndex((candidate) => candidate.id === edgeId);
  return index < 0 ? null : { index, count: cases.length };
}

export function getEdgeRoutingLabel(
  edge: Edge<AgentflowEdgeData>,
  edges: Edge<AgentflowEdgeData>[],
) {
  const data = { ...createDefaultEdgeData(), ...edge.data };
  if (data.kind === AgentflowEdgeKind.SwitchDefault) return "ELSE";
  if (data.kind === AgentflowEdgeKind.SwitchCase) {
    return getSwitchCasePosition(edges, edge.id)?.index === 0 ? "IF" : "ELSE IF";
  }
  if (data.kind === AgentflowEdgeKind.FanInBarrier) return "BARRIER";
  if (data.kind === AgentflowEdgeKind.FanOut) {
    return data.conditionJson.trim() ? "WHEN" : "FAN OUT";
  }
  return "";
}

export function validateAgentflowEdgeRouting(edges: Edge<AgentflowEdgeData>[]) {
  const sourceIds = new Set(edges.map((edge) => edge.source));
  for (const sourceId of sourceIds) {
    const sourceEdges = edges.filter(
      (edge) => edge.source === sourceId && edge.data?.kind !== AgentflowEdgeKind.FanInBarrier,
    );
    const strategies = new Set(
      sourceEdges
        .map((edge) => getRoutingStrategy(edge.data?.kind))
        .filter((strategy): strategy is RoutingStrategy => strategy !== null),
    );
    if (strategies.size > 1) {
      return `${sourceId} cannot mix Direct, Fan Out, and Switch routing`;
    }

    if (!strategies.has("switch")) continue;

    const cases = sourceEdges.filter((edge) => edge.data?.kind === AgentflowEdgeKind.SwitchCase);
    const defaults = sourceEdges.filter(
      (edge) => edge.data?.kind === AgentflowEdgeKind.SwitchDefault,
    );
    if (cases.length === 0) return `${sourceId} needs at least one If branch`;
    if (defaults.length > 1) return `${sourceId} can only have one Else branch`;
    if (defaults.some((edge) => edge.data?.conditionJson.trim())) {
      return `${sourceId} Else branch cannot have a predicate`;
    }

    const orders = new Set<number>();
    for (const edge of cases) {
      if (!edge.data?.conditionJson.trim()) return `${edge.id} needs a predicate`;
      const order = getSwitchCaseOrder(edge.data.configJson);
      if (order === null || orders.has(order)) return `${sourceId} has invalid branch order`;
      orders.add(order);
    }
  }

  return null;
}

function getRoutingStrategy(kind: AgentflowEdgeKind | undefined): RoutingStrategy | null {
  if (kind === AgentflowEdgeKind.Direct) return "direct";
  if (kind === AgentflowEdgeKind.FanOut) return "fan-out";
  if (kind === AgentflowEdgeKind.SwitchCase || kind === AgentflowEdgeKind.SwitchDefault) {
    return "switch";
  }
  return null;
}

function readConfigJson(value: string): Record<string, unknown> | null {
  if (!value.trim()) return {};

  try {
    const parsed = JSON.parse(value) as unknown;
    return typeof parsed === "object" && parsed !== null && !Array.isArray(parsed)
      ? (parsed as Record<string, unknown>)
      : null;
  } catch {
    return null;
  }
}
