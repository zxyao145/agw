"use client";

import * as React from "react";
import { Brain, Lightbulb, Plus, ShieldAlert, Sparkles, Wrench, X } from "lucide-react";

import {
  Button,
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  Separator,
  cn,
} from "@agw/components";
import type { AgentCommandSuggestion, CommandSource } from "../../../lib/chat/search-command";
import type { AgentMode, PermissionMode } from "../../../services/execution-hub";

type ChatInputToolbarProps = {
  commandSource: CommandSource;
  isExecuting: boolean;
  isTransitioning: boolean;
  permissionMode: PermissionMode;
  agentMode: AgentMode;
  onCommandSelect: (command: string) => void;
  onPermissionModeChange: (mode: PermissionMode) => void;
  onAgentModeChange: (mode: AgentMode) => void;
};

const permissionLabels: Record<PermissionMode, string> = {
  fullAccess: "Full access",
  alwaysAsk: "Always ask",
  allowSameArguments: "Allow same arguments",
};

export function ChatInputToolbar({
  commandSource,
  isExecuting,
  isTransitioning,
  permissionMode,
  agentMode,
  onCommandSelect,
  onPermissionModeChange,
  onAgentModeChange,
}: ChatInputToolbarProps) {
  const suggestions = commandSource.mode === "system" ? commandSource.suggestions : [];
  const skills = suggestions.filter((suggestion) => suggestion.kind === "skill");
  const tools = suggestions.filter(
    (suggestion) =>
      suggestion.kind === "tool" &&
      suggestion.text !== "/mode_get" &&
      suggestion.text !== "/mode_set",
  );
  const supportsMode =
    commandSource.mode === "system" &&
    commandSource.suggestions.some((suggestion) => suggestion.text === "/mode_set");
  const hasAddItems = supportsMode || skills.length > 0 || tools.length > 0;

  return (
    <div className="flex min-w-0 items-center gap-1">
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button
            type="button"
            variant="ghost"
            size="icon-sm"
            className="size-7 shrink-0 rounded-full hover:bg-muted"
            aria-label="Add"
          >
            <Plus className="size-5" />
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent
          side="top"
          align="start"
          sideOffset={10}
          className="w-[min(24rem,calc(100vw-3rem))] max-h-[min(32rem,65vh)] rounded-2xl p-2 shadow-xl"
        >
          <DropdownMenuLabel className="px-2 py-1 text-xs font-normal text-muted-foreground">
            Add
          </DropdownMenuLabel>
          {supportsMode ? (
            <DropdownMenuItem
              disabled={isTransitioning}
              className="min-h-11 cursor-pointer rounded-xl px-2.5"
              onSelect={() => onAgentModeChange(agentMode === "plan" ? "execute" : "plan")}
            >
              <Lightbulb className="size-4" />
              <span className="min-w-0">
                <span className="block font-medium">Plan mode</span>
                <span className="block truncate text-xs text-muted-foreground">
                  Turn plan mode {agentMode === "plan" ? "off" : "on"}
                </span>
              </span>
            </DropdownMenuItem>
          ) : null}

          <CapabilityGroup
            label="Skills"
            items={skills}
            disabled={isExecuting || isTransitioning}
            onSelect={onCommandSelect}
          />
          <CapabilityGroup
            label="Tools"
            items={tools}
            disabled={isExecuting || isTransitioning}
            onSelect={onCommandSelect}
          />

          {!hasAddItems ? (
            <div className="px-2.5 py-4 text-sm text-muted-foreground">
              No actions are available for this target.
            </div>
          ) : null}
        </DropdownMenuContent>
      </DropdownMenu>

      <Select
        value={permissionMode}
        onValueChange={(value) => onPermissionModeChange(value as PermissionMode)}
        disabled={isTransitioning}
      >
        <SelectTrigger
          size="sm"
          className={cn(
            "data-[size=sm]:h-7 w-auto max-w-52 gap-1.5 border-0 bg-transparent px-2 shadow-none focus-visible:ring-0 rounded-full hover:bg-muted focus-within:bg-muted",
            permissionMode === "fullAccess" &&
              "text-orange-600 hover:text-orange-600 dark:text-orange-400 dark:hover:text-orange-400",
          )}
          aria-label="Tool permission mode"
        >
          <ShieldAlert className="size-4 shrink-0" />
          <SelectValue>{permissionLabels[permissionMode]}</SelectValue>
        </SelectTrigger>
        <SelectContent position="popper" align="start">
          {(Object.keys(permissionLabels) as PermissionMode[]).map((mode) => (
            <SelectItem key={mode} value={mode}>
              {permissionLabels[mode]}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>

      {supportsMode && agentMode === "plan" ? (
        <>
          <Separator orientation="vertical" className="mx-1 data-[orientation=vertical]:h-5" />
          <div className="cursor-pointer group flex h-7 items-center gap-1.5 rounded-full px-2 py-1 text-sm text-muted-foreground transition-colors hover:bg-muted focus-within:bg-muted">
            <span className="relative size-4 shrink-0">
              <Brain className="absolute inset-0 size-4 transition-opacity group-hover:opacity-0 group-focus-within:opacity-0" />
              <button
                type="button"
                disabled={isTransitioning}
                aria-label="Turn plan mode off"
                className="cursor-pointer absolute -inset-0.5 flex size-5 items-center justify-center rounded-full bg-muted-foreground text-background opacity-0 transition-opacity group-hover:opacity-100 group-focus-within:opacity-100 focus-visible:opacity-100 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
                onClick={() => onAgentModeChange("execute")}
              >
                <X className="size-3" />
              </button>
            </span>
            <span>Plan</span>
          </div>
        </>
      ) : null}
    </div>
  );
}

function CapabilityGroup({
  label,
  items,
  disabled,
  onSelect,
}: {
  label: "Skills" | "Tools";
  items: AgentCommandSuggestion[];
  disabled: boolean;
  onSelect: (command: string) => void;
}) {
  if (items.length === 0) return null;
  const Icon = label === "Skills" ? Sparkles : Wrench;

  return (
    <>
      <DropdownMenuSeparator />
      <DropdownMenuLabel className="px-2 py-1 text-xs font-normal text-muted-foreground">
        {label}
      </DropdownMenuLabel>
      {items.map((item) => (
        <DropdownMenuItem
          key={`${item.kind}:${item.text}`}
          disabled={disabled}
          className="min-h-10 cursor-pointer rounded-xl px-2.5"
          onSelect={() => onSelect(item.text)}
        >
          <Icon className="size-4" />
          <span className="min-w-0">
            <span className="block truncate font-medium">{item.text}</span>
            {item.description ? (
              <span className="block truncate text-xs text-muted-foreground">
                {item.description}
              </span>
            ) : null}
          </span>
        </DropdownMenuItem>
      ))}
    </>
  );
}
