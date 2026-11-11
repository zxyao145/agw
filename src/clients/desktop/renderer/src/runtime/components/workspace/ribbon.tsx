"use client";

import { cn, Tooltip, TooltipContent, TooltipTrigger } from "@agw/components";

import type { WorkspaceEntry } from "../../extensions/workspace-registry";
import { WorkspaceIconView } from "./workspace-icon";

export type RibbonProps = {
  entries: readonly WorkspaceEntry[];
  sidebarVisible: boolean;
  activeSidebarViewId: string;
  activeMainViewId: string;
  unreadNotificationCount?: number;
  onSelect(entry: WorkspaceEntry): void;
};

function RibbonButton({
  entry,
  active,
  unreadNotificationCount,
  onSelect,
}: {
  entry: WorkspaceEntry;
  active: boolean;
  unreadNotificationCount: number;
  onSelect(entry: WorkspaceEntry): void;
}) {
  const label = entry.extensionName ? `${entry.title} (${entry.extensionName})` : entry.title;
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <button
          type="button"
          className={cn("agw-ribbon-item relative", active && "is-active")}
          aria-label={label}
          aria-pressed={entry.action.type !== "command" ? active : undefined}
          data-entry-id={entry.id}
          onClick={() => onSelect(entry)}
        >
          <WorkspaceIconView icon={entry.icon} />
          {entry.id === "agw.ribbon.notifications" && unreadNotificationCount > 0 ? (
            <span className="pointer-events-none absolute -right-1 -top-1">
              <span className="inline-flex min-w-5 items-center justify-center rounded-full bg-primary px-1.5 py-0.5 text-[10px] font-semibold leading-none text-primary-foreground">
                {unreadNotificationCount > 99 ? "99+" : unreadNotificationCount}
              </span>
            </span>
          ) : null}
        </button>
      </TooltipTrigger>
      <TooltipContent side="right">{label}</TooltipContent>
    </Tooltip>
  );
}

/** 窗口左侧的竖向图标栏：内置项在前，extension 项在后。The vertical icon bar: built-in items first, then extensions. */
export function Ribbon({
  entries,
  sidebarVisible,
  activeSidebarViewId,
  activeMainViewId,
  unreadNotificationCount = 0,
  onSelect,
}: RibbonProps) {
  const builtIn = entries.filter((entry) => entry.extensionId === null);
  const extensions = entries.filter((entry) => entry.extensionId !== null);
  const isActive = (entry: WorkspaceEntry) =>
    entry.action.type === "openMainView"
      ? entry.action.view === activeMainViewId
      : entry.action.type === "toggleSidebarView" &&
        sidebarVisible &&
        entry.action.view === activeSidebarViewId;

  return (
    <nav className="agw-ribbon" aria-label="Ribbon">
      {builtIn.map((entry) => (
        <RibbonButton
          key={entry.id}
          entry={entry}
          active={isActive(entry)}
          unreadNotificationCount={unreadNotificationCount}
          onSelect={onSelect}
        />
      ))}
      {extensions.length > 0 ? <div role="separator" className="agw-ribbon-separator" /> : null}
      {extensions.map((entry) => (
        <RibbonButton
          key={entry.id}
          entry={entry}
          active={isActive(entry)}
          unreadNotificationCount={unreadNotificationCount}
          onSelect={onSelect}
        />
      ))}
    </nav>
  );
}
