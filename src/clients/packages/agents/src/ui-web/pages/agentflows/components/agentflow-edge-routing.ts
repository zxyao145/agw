import type { AgentflowEdge, AgentflowRoutingStrategy } from "../../../../types/agentflow";
import type { AgentflowEditorDocument } from "./agentflow-document";

function sortSwitchCases(edges: AgentflowEdge[]): AgentflowEdge[] {
  return edges.sort((left, right) => left.switchCaseOrder - right.switchCaseOrder);
}

export function getRegularOutgoingEdges(edges: readonly AgentflowEdge[], sourceNodeId: string) {
  return edges.filter(
    (edge) => edge.sourceNodeId === sourceNodeId && !edge.isFanInBarrier && !edge.isSwitchDefault,
  );
}

export function getSwitchCases(edges: readonly AgentflowEdge[], sourceNodeId: string) {
  return sortSwitchCases(getRegularOutgoingEdges(edges, sourceNodeId));
}

function nextSwitchCaseOrder(edges: readonly AgentflowEdge[], sourceNodeId: string) {
  return (getSwitchCases(edges, sourceNodeId).at(-1)?.switchCaseOrder ?? -1) + 1;
}

export function getNewEdgeRouting(document: AgentflowEditorDocument, sourceNodeId: string) {
  const source = document.nodes.find((node) => node.nodeId === sourceNodeId);
  const switchCaseOrder =
    source?.routingStrategy === "switch" ? nextSwitchCaseOrder(document.edges, sourceNodeId) : 0;
  if (source?.kind !== "humanGate") return { switchCaseOrder, choiceId: null };

  const assignedChoices = new Set(
    document.edges
      .filter((edge) => edge.sourceNodeId === sourceNodeId && !edge.isFanInBarrier)
      .map((edge) => edge.choiceId),
  );
  const choice = source.choices.find((item) => !assignedChoices.has(item.choiceId));
  return { switchCaseOrder, choiceId: choice?.choiceId ?? source.choices[0]?.choiceId ?? null };
}

export function renumberSwitchCases(
  document: AgentflowEditorDocument,
  sourceNodeIds?: readonly string[],
): AgentflowEditorDocument {
  const sourceIds =
    sourceNodeIds ??
    document.nodes.filter((node) => node.routingStrategy === "switch").map((node) => node.nodeId);
  const selectedSources = new Set(sourceIds);
  const nextOrderByEdgeId = new Map<string, number>();

  for (const sourceNodeId of sourceIds) {
    const cases = getSwitchCases(document.edges, sourceNodeId);
    cases.forEach((edge, index) => nextOrderByEdgeId.set(edge.edgeId, index));
  }

  return {
    ...document,
    edges: document.edges.map((edge) => {
      if (!selectedSources.has(edge.sourceNodeId)) return edge;
      if (edge.isFanInBarrier || edge.isSwitchDefault) return edge;
      return { ...edge, switchCaseOrder: nextOrderByEdgeId.get(edge.edgeId) ?? 0 };
    }),
  };
}

export function setRoutingStrategy(
  document: AgentflowEditorDocument,
  nodeId: string,
  strategy: AgentflowRoutingStrategy,
): AgentflowEditorDocument {
  const nodes = document.nodes.map((node) =>
    node.nodeId === nodeId ? { ...node, routingStrategy: strategy } : node,
  );
  const edges = document.edges.map((edge) => {
    if (edge.sourceNodeId !== nodeId || edge.isFanInBarrier) return edge;
    if (strategy === "switch") return edge;
    return { ...edge, isSwitchDefault: false, switchCaseOrder: 0 };
  });

  return strategy === "switch"
    ? renumberSwitchCases({ ...document, nodes, edges }, [nodeId])
    : { ...document, nodes, edges };
}

export function setSwitchDefault(
  document: AgentflowEditorDocument,
  edgeId: string,
): AgentflowEditorDocument {
  const selected = document.edges.find((edge) => edge.edgeId === edgeId);
  if (!selected) return document;

  const siblings = sortSwitchCases(
    document.edges.filter(
      (edge) => edge.sourceNodeId === selected.sourceNodeId && !edge.isFanInBarrier,
    ),
  );
  let order = 0;
  const edges = siblings.map((edge) => {
    if (edge.edgeId === edgeId) {
      return { ...edge, isSwitchDefault: true, switchCaseOrder: 0, conditions: [] };
    }
    return {
      ...edge,
      isSwitchDefault: false,
      switchCaseOrder: order++,
    };
  });
  const byId = new Map(edges.map((edge) => [edge.edgeId, edge]));
  return { ...document, edges: document.edges.map((edge) => byId.get(edge.edgeId) ?? edge) };
}

export function clearSwitchDefault(
  document: AgentflowEditorDocument,
  edgeId: string,
): AgentflowEditorDocument {
  const selected = document.edges.find((edge) => edge.edgeId === edgeId);
  if (!selected) return document;
  const nextOrder = nextSwitchCaseOrder(document.edges, selected.sourceNodeId);

  return {
    ...document,
    edges: document.edges.map((edge) =>
      edge.edgeId === edgeId
        ? { ...edge, isSwitchDefault: false, switchCaseOrder: nextOrder }
        : edge,
    ),
  };
}

export function reorderSwitchCase(
  document: AgentflowEditorDocument,
  edgeId: string,
  targetIndex: number,
): AgentflowEditorDocument {
  const selected = document.edges.find((edge) => edge.edgeId === edgeId);
  if (!selected) return document;

  const cases = getSwitchCases(document.edges, selected.sourceNodeId);
  const currentIndex = cases.findIndex((edge) => edge.edgeId === edgeId);
  if (currentIndex < 0) return document;
  const [moved] = cases.splice(currentIndex, 1);
  cases.splice(Math.max(0, Math.min(targetIndex, cases.length)), 0, moved);
  const orderById = new Map(cases.map((edge, index) => [edge.edgeId, index]));

  return {
    ...document,
    edges: document.edges.map((edge) => {
      const switchCaseOrder = orderById.get(edge.edgeId);
      return switchCaseOrder === undefined ? edge : { ...edge, switchCaseOrder };
    }),
  };
}

export function setFanInBarrier(
  document: AgentflowEditorDocument,
  edgeId: string,
  waiting: boolean,
): AgentflowEditorDocument {
  return {
    ...document,
    edges: document.edges.map((edge) => {
      if (edge.edgeId !== edgeId || edge.isFanInBarrier === waiting) return edge;
      return waiting
        ? {
            ...edge,
            isFanInBarrier: true,
            isSwitchDefault: false,
            switchCaseOrder: 0,
            choiceId: null,
            conditions: [],
          }
        : { ...edge, isFanInBarrier: false, ...getNewEdgeRouting(document, edge.sourceNodeId) };
    }),
  };
}

export function removeGateChoice(
  document: AgentflowEditorDocument,
  nodeId: string,
  choiceId: string,
): AgentflowEditorDocument {
  return {
    ...document,
    nodes: document.nodes.map((node) =>
      node.nodeId === nodeId && node.kind === "humanGate"
        ? { ...node, choices: node.choices.filter((choice) => choice.choiceId !== choiceId) }
        : node,
    ),
    edges: document.edges.filter(
      (edge) => !(edge.sourceNodeId === nodeId && edge.choiceId === choiceId),
    ),
  };
}
