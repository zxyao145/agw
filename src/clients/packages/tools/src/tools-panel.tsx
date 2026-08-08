"use client";

import * as React from "react";
import type { UseQueryResult } from "@agw/components/query";
import { CheckSquare2, Puzzle, X } from "lucide-react";

import {
  Badge,
  Button,
  Checkbox,
  Label,
  SearchableSelect,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  cn,
  type SearchableSelectOption,
} from "@agw/components";

import {
  createToolBlockValue,
  createToolValue,
  type BackgroundAgentsToolBlockDefinition,
  type ProjectMemoryToolBlockDefinition,
  type ToolBlockName,
  type ToolInfo,
  type ToolName,
  type ToolValueObject,
} from "./tool-values";

type AgentOption = { id: string; name: string; displayName?: string };

type ToolsPanelProps = {
  scope: "agent" | "project";
  idPrefix?: string;
  ownerLabel?: string;
  toolsQuery: UseQueryResult<ToolInfo[], Error>;
  values: ToolValueObject[];
  setValues: (value: ToolValueObject[]) => void;
  disabled?: boolean;
  notice?: React.ReactNode;
  agentOptions?: AgentOption[];
};

const AGENT_SCOPE = 1;
const PROJECT_SCOPE = 2;

export function ToolsPanel({
  scope,
  idPrefix = "",
  ownerLabel = scope,
  toolsQuery,
  values,
  setValues,
  disabled = false,
  notice,
  agentOptions = [],
}: ToolsPanelProps) {
  const catalog = toolsQuery.data ?? [];
  const individualTools = catalog.filter((item) => item.kind === "tool");
  const visibleToolBlocks = catalog.filter(
    (item) =>
      item.kind === "toolBlock" &&
      (item.scopes & (scope === "agent" ? AGENT_SCOPE : PROJECT_SCOPE)) !== 0,
  );
  const selectedTools = values
    .filter((value) => value.kind === "tool")
    .map((value) => value.definition.name);
  const toolBlocks = values.filter((value) => value.kind === "toolBlock");
  const toolOptions = React.useMemo<SearchableSelectOption[]>(
    () =>
      individualTools
        .map((tool) => ({
          value: tool.name,
          title: tool.displayName || tool.name,
          subtitle: tool.description,
          group: tool.category.trim() || "Uncategorized",
        }))
        .sort((left, right) => (left.group ?? "").localeCompare(right.group ?? "")),
    [individualTools],
  );

  const toggleToolBlock = (name: ToolBlockName) => {
    if (disabled) return;
    const selected = toolBlocks.some((value) => value.definition.name === name);
    setValues(
      selected
        ? values.filter((value) => value.kind !== "toolBlock" || value.definition.name !== name)
        : [...values, createToolBlockValue(name)],
    );
  };

  const updateProjectMemoryStorage = (storage: "database" | "filesystem") => {
    setValues(
      values.map((value) =>
        value.kind === "toolBlock" && value.definition.name === "project-memory"
          ? { ...value, definition: { ...value.definition, options: { storage } } }
          : value,
      ),
    );
  };

  const updateBackgroundAgentIds = (allowedAgentIds: string[]) => {
    setValues(
      values.map((value) =>
        value.kind === "toolBlock" && value.definition.name === "background-agents"
          ? { ...value, definition: { ...value.definition, options: { allowedAgentIds } } }
          : value,
      ),
    );
  };

  const toggleTool = (name: ToolName) => {
    if (disabled) return;
    const selected = selectedTools.some((selectedName) => selectedName === name);
    setValues(
      selected
        ? values.filter((value) => value.kind !== "tool" || value.definition.name !== name)
        : [...values, createToolValue(name)],
    );
  };

  return (
    <div className="space-y-8">
      {notice}

      <section className="space-y-4">
        <div>
          <h3 className="font-medium">Tool Blocks</h3>
          <p className="mt-1 text-sm text-muted-foreground">
            Add or remove each atomic group as a whole. Member tools cannot be selected
            independently.
          </p>
        </div>

        {toolsQuery.isLoading ? (
          <p className="text-sm text-muted-foreground">Loading Tool Blocks...</p>
        ) : visibleToolBlocks.length === 0 ? (
          <p className="rounded-lg border border-dashed p-4 text-sm text-muted-foreground">
            No Tool Blocks are available for this {ownerLabel}.
          </p>
        ) : (
          <div className="grid gap-3 xl:grid-cols-2">
            {visibleToolBlocks.map((item) => {
              const value = toolBlocks.find((candidate) => candidate.definition.name === item.name);
              const definition = value?.definition;
              const selected = Boolean(value);
              const projectMemoryDefinition =
                definition?.name === "project-memory"
                  ? (definition as ProjectMemoryToolBlockDefinition)
                  : undefined;
              const backgroundDefinition =
                definition?.name === "background-agents"
                  ? (definition as BackgroundAgentsToolBlockDefinition)
                  : undefined;

              return (
                <div
                  key={item.name}
                  className={cn(
                    "rounded-xl border bg-background p-4 transition-colors",
                    selected && !disabled && "border-primary/60 bg-primary/[0.035]",
                    disabled && "opacity-60",
                  )}
                >
                  <div className="flex items-start gap-3">
                    <div className="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg border bg-muted/40">
                      <CheckSquare2 className="h-4 w-4" />
                    </div>
                    <div className="min-w-0 flex-1">
                      <div className="flex flex-wrap items-center gap-2">
                        <Label
                          htmlFor={`${idPrefix}tool-block-${item.name}`}
                          className="font-medium"
                        >
                          {item.displayName || item.name}
                        </Label>
                        {item.requiresConfirmation ? (
                          <Badge variant="outline">Approval</Badge>
                        ) : null}
                        {item.memberToolNames.length > 0 ? (
                          <Badge variant="secondary">{item.memberToolNames.length} tools</Badge>
                        ) : null}
                      </div>
                      <p className="mt-1 text-xs leading-relaxed text-muted-foreground">
                        {item.description}
                      </p>
                      {item.memberToolNames.length > 0 ? (
                        <p className="mt-2 font-mono text-[11px] text-muted-foreground">
                          {item.memberToolNames.join(", ")}
                        </p>
                      ) : null}
                    </div>
                    <Checkbox
                      id={`${idPrefix}tool-block-${item.name}`}
                      checked={selected}
                      onCheckedChange={() => toggleToolBlock(item.name as ToolBlockName)}
                      disabled={disabled}
                    />
                  </div>

                  {selected && item.name === "project-memory" ? (
                    <div className="mt-4 border-t pt-3">
                      <Label className="text-xs">Storage</Label>
                      <Select
                        value={projectMemoryDefinition?.options?.storage ?? "database"}
                        onValueChange={(storage) =>
                          updateProjectMemoryStorage(
                            storage === "filesystem" ? "filesystem" : "database",
                          )
                        }
                        disabled={disabled}
                      >
                        <SelectTrigger className="mt-1.5 w-full">
                          <SelectValue />
                        </SelectTrigger>
                        <SelectContent position="popper">
                          <SelectItem value="database">Database</SelectItem>
                          <SelectItem value="filesystem">
                            Project Workspace (.agw/memory)
                          </SelectItem>
                        </SelectContent>
                      </Select>
                    </div>
                  ) : null}

                  {selected && item.name === "background-agents" ? (
                    <div className="mt-4 space-y-2 border-t pt-3">
                      <Label className="text-xs">Allowed delegation targets</Label>
                      {agentOptions.length === 0 ? (
                        <p className="text-xs text-muted-foreground">
                          No other System Agents available.
                        </p>
                      ) : (
                        agentOptions.map((agent) => {
                          const allowedAgentIds =
                            backgroundDefinition?.options?.allowedAgentIds ?? [];
                          return (
                            <Label
                              key={agent.id}
                              className="flex items-center gap-2 rounded-md px-2 py-1.5 hover:bg-muted/40"
                            >
                              <Checkbox
                                checked={allowedAgentIds.includes(agent.id)}
                                disabled={disabled}
                                onCheckedChange={() =>
                                  updateBackgroundAgentIds(
                                    allowedAgentIds.includes(agent.id)
                                      ? allowedAgentIds.filter((id) => id !== agent.id)
                                      : [...allowedAgentIds, agent.id],
                                  )
                                }
                              />
                              <span className="text-sm font-normal">
                                {agent.displayName || agent.name}
                              </span>
                            </Label>
                          );
                        })
                      )}
                    </div>
                  ) : null}
                </div>
              );
            })}
          </div>
        )}
      </section>

      <section className="space-y-4 border-t pt-6">
        <div>
          <h3 className="font-medium">Tools</h3>
          <p className="mt-1 text-sm text-muted-foreground">
            Select standalone tools independently. Web Search automatically uses hosted search when
            supported and falls back to local search.
          </p>
        </div>
        <SearchableSelect
          multiple
          id={`${idPrefix}tools`}
          ariaLabel="Tools"
          value={selectedTools}
          onValueChange={(values) => {
            const nextNames = Array.isArray(values) ? values : [];
            setValues([
              ...toolBlocks,
              ...nextNames.map((name) => createToolValue(name as ToolName)),
            ]);
          }}
          options={toolOptions}
          placeholder="Select Tools..."
          selectionText={
            selectedTools.length > 0
              ? `${selectedTools.length} tool${selectedTools.length === 1 ? "" : "s"} selected`
              : undefined
          }
          searchPlaceholder="Search Tools..."
          disabled={disabled}
          isLoading={toolsQuery.isLoading}
          clearable={false}
        />

        {selectedTools.length === 0 ? (
          <p className="rounded-lg border border-dashed p-4 text-sm text-muted-foreground">
            No Tools selected.
          </p>
        ) : (
          <div className="space-y-2">
            {selectedTools.map((name) => {
              const tool = individualTools.find((candidate) => candidate.name === name);
              return (
                <div
                  key={name}
                  className="flex items-center gap-3 rounded-lg border bg-background px-3 py-2"
                >
                  <Puzzle className="h-4 w-4 shrink-0 text-muted-foreground" />
                  <div className="min-w-0 flex-1">
                    <p className="truncate text-sm font-medium">{tool?.displayName || name}</p>
                    {tool?.description ? (
                      <p className="truncate text-xs text-muted-foreground">{tool.description}</p>
                    ) : null}
                  </div>
                  {!disabled ? (
                    <Button
                      type="button"
                      variant="ghost"
                      size="icon"
                      className="h-7 w-7"
                      aria-label={`Remove ${name}`}
                      onClick={() => toggleTool(name)}
                    >
                      <X className="h-4 w-4" />
                    </Button>
                  ) : null}
                </div>
              );
            })}
          </div>
        )}
      </section>
    </div>
  );
}
