"use client";

import * as React from "react";
import { useQuery } from "@agw/components/query";

import { apiGet } from "@agw/api";
import { getApiErrorMessage } from "@agw/api";
import { SearchableSelect, type SearchableSelectOption } from "@agw/components";
import { buildChatTargetOptions, getTargetValue, parseTargetValue } from "@agw/api";

type AgentDto = {
  id: string;
  displayName: string;
  name: string;
  enable: boolean;
};

type AgentflowDto = {
  id: string;
  name: string;
  enable: boolean;
};

export type AgentSelection = {
  agentType: 0 | 1;
  agentId: string;
};

export type AgentSelectorProps = {
  id: string;
  projectId?: string | null;
  value?: AgentSelection | null;
  onSelect: (selection: AgentSelection) => void;
  onClear?: () => void;
  clearable?: boolean;
  placeholder?: string;
  size?: "default" | "sm";
};

export function AgentSelector({
  id,
  projectId,
  value,
  onSelect,
  onClear,
  clearable = false,
  placeholder = "Select agent or agentflow",
  size = "default",
}: AgentSelectorProps) {
  const agentsQuery = useQuery({
    queryKey: ["agents"],
    queryFn: async () => (await apiGet("/api/agents")) as AgentDto[],
  });
  const agentflowsQuery = useQuery({
    queryKey: ["agentflows"],
    queryFn: async () => (await apiGet("/api/agentflows")) as AgentflowDto[],
  });

  const options = React.useMemo<SearchableSelectOption[]>(() => {
    const agentSearchKeywords = new Map<string, string[]>();
    for (const agent of agentsQuery.data ?? []) {
      agentSearchKeywords.set(agent.id, [agent.name, agent.displayName]);
    }

    return buildChatTargetOptions({
      projectId: projectId ?? null,
      agents: agentsQuery.data ?? [],
      agentflows: agentflowsQuery.data ?? [],
    }).map((option) => ({
      value: getTargetValue(option),
      title: option.label,
      keywords: option.type === "agent" ? agentSearchKeywords.get(option.id) : undefined,
      group: option.type === "agent" ? "Agent" : "Agentflow",
    }));
  }, [agentflowsQuery.data, agentsQuery.data, projectId]);

  const selectedValue = value
    ? getTargetValue({
        id: value.agentId,
        type: value.agentType === 0 ? "agent" : "agentflow",
      })
    : "";
  const queryError = agentsQuery.error ?? agentflowsQuery.error;

  const handleValueChange = (targetValue: string) => {
    const target = parseTargetValue(targetValue);
    if (!target) {
      onClear?.();
      return;
    }

    onSelect({
      agentType: target.type === "agent" ? 0 : 1,
      agentId: target.id,
    });
  };

  return (
    <SearchableSelect
      id={id}
      ariaLabel="Select target"
      value={selectedValue}
      onValueChange={handleValueChange}
      options={options}
      placeholder={placeholder}
      searchPlaceholder="Search agents or agentflows..."
      isLoading={agentsQuery.isLoading || agentflowsQuery.isLoading}
      errorMessage={queryError ? getApiErrorMessage(queryError) : null}
      clearable={clearable}
      size={size}
    />
  );
}
