import type {
  AgentflowConditionRule,
  AgentflowEdge,
  AgentflowNode,
} from "../../../../types/agentflow";
import { getRegularOutgoingEdges } from "./agentflow-edge-routing";

export type AgentflowConditionField = AgentflowConditionRule["field"];

const CONDITION_FIELDS: AgentflowConditionField[] = [
  "contains",
  "notContains",
  "equals",
  "author",
  "role",
  "minMessages",
];

export function describeConditions(rules: readonly AgentflowConditionRule[]): string {
  return rules.map(describeCondition).join(" and ");
}

export function addConditionRule(
  rules: AgentflowConditionRule[],
  field: AgentflowConditionField,
): AgentflowConditionRule[] {
  return [...rules, { field, value: field === "minMessages" ? "1" : "", path: "" }];
}

export function updateConditionRule(
  rules: AgentflowConditionRule[],
  index: number,
  patch: Partial<AgentflowConditionRule>,
): AgentflowConditionRule[] {
  return rules.map((rule, ruleIndex) => (ruleIndex === index ? { ...rule, ...patch } : rule));
}

export function removeConditionRule(
  rules: AgentflowConditionRule[],
  index: number,
): AgentflowConditionRule[] {
  return rules.filter((_, ruleIndex) => ruleIndex !== index);
}

export function getAvailableConditionFields(
  rules: AgentflowConditionRule[],
  sourceProducesResult: boolean,
): AgentflowConditionField[] {
  return [
    ...CONDITION_FIELDS.filter((field) => !rules.some((rule) => rule.field === field)),
    ...(sourceProducesResult ? ["result" as const] : []),
  ];
}

export function describeAgentflowEdge(
  edge: AgentflowEdge,
  nodes: AgentflowNode[],
  edges: AgentflowEdge[],
): string {
  if (edge.isFanInBarrier) return "wait";

  const source = nodes.find((node) => node.nodeId === edge.sourceNodeId);
  if (!source) return "";

  if (source.kind === "humanGate") {
    return source.choices.find((choice) => choice.choiceId === edge.choiceId)?.label ?? "";
  }

  if (source.routingStrategy === "switch") {
    if (edge.isSwitchDefault) return "else";

    const hasPreviousBranch = getRegularOutgoingEdges(edges, edge.sourceNodeId).some(
      (candidate) => candidate.switchCaseOrder < edge.switchCaseOrder,
    );
    const prefix = hasPreviousBranch ? "else if" : "if";
    return `${prefix} ${describeConditions(edge.conditions)}`;
  }

  const condition = describeConditions(edge.conditions);
  return condition ? `when ${condition}` : "";
}

function describeCondition(rule: AgentflowConditionRule): string {
  switch (rule.field) {
    case "contains":
      return `contains "${rule.value}"`;
    case "notContains":
      return `does not contain "${rule.value}"`;
    case "equals":
      return `equals "${rule.value}"`;
    case "author":
      return `author is ${rule.value}`;
    case "role":
      return `role is ${rule.value}`;
    case "minMessages":
      return `at least ${rule.value} messages`;
    case "result":
      return `${rule.path} is "${rule.value}"`;
    default:
      return `${rule.path ? `${rule.path} ` : ""}${rule.field} "${rule.value}"`;
  }
}
