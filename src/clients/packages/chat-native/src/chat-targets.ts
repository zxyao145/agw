import { getTargetValue, type ChatTargetOption, type ChatTargetType } from "@agw/api";

export type ChatTargetGroup = {
  label: "Agentflow" | "Agent";
  type: ChatTargetType;
  targets: ChatTargetOption[];
};

const targetGroupDefinitions: ReadonlyArray<Pick<ChatTargetGroup, "label" | "type">> = [
  { label: "Agent", type: "agent" },
  { label: "Agentflow", type: "agentflow" },
];

export function groupChatTargets(targets: ChatTargetOption[]): ChatTargetGroup[] {
  return targetGroupDefinitions.flatMap((group) => {
    const groupedTargets = targets.filter((target) => target.type === group.type);
    return groupedTargets.length > 0 ? [{ ...group, targets: groupedTargets }] : [];
  });
}

export function getDefaultChatTargetValue(targets: ChatTargetOption[]): string | null {
  const target = targets.find((item) => item.type === "agent") ?? targets[0];
  return target ? getTargetValue(target) : null;
}
