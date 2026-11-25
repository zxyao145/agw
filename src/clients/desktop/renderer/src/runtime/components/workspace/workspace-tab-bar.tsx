"use client";

import * as React from "react";
import {
  cn,
  ContextMenu,
  ContextMenuContent,
  ContextMenuItem,
  ContextMenuSeparator,
  ContextMenuTrigger,
} from "@agw/components";
import type { ConversationStatus, ExecutionStatus } from "@agw/chat-runtime";
import { shouldShowFileDiff, type ProjectDirectoryOption } from "@agw/projects";
import { X } from "lucide-react";

import { fileBaseName } from "../../extensions/file-viewer-patterns";
import type { WorkspaceIcon, WorkspaceView } from "../../extensions/workspace-registry";
import {
  hasVisibleWorkspaceTabs,
  type ChatTab,
  type FileTab,
  type TabGroup,
  type WorkspaceTab,
} from "../../extensions/workspace-tab";
import { StatusDot } from "../execution-status-dot";
import { useHorizontalTabWheel } from "../use-horizontal-tab-wheel";
import { WorkspaceIconView } from "./workspace-icon";

export type WorkspaceTabView = {
  tab: WorkspaceTab;
  title: string;
  /** 同名文件的上级路径或目录名称。The parent path or directory name that distinguishes same-named files. */
  description: string | null;
  tooltip: string;
  icon: WorkspaceIcon;
  status: ExecutionStatus | null;
  unsaved: boolean;
};

export type TabDescribeContext = {
  directories: readonly ProjectDirectoryOption[];
  onlyDiff?: boolean;
  conversationStatuses: ReadonlyMap<string, ConversationStatus | ExecutionStatus>;
  unsavedTabIds: ReadonlySet<string>;
  view(viewId: string): WorkspaceView | undefined;
};

function fileDescription(
  tab: FileTab,
  peers: readonly FileTab[],
  directoryName: string | undefined,
): string | null {
  const others = peers.filter((peer) => peer !== tab);
  if (others.length === 0) return null;
  if (others.some((other) => other.path === tab.path))
    return directoryName ?? tab.directoryId ?? ".";

  const parents = tab.path.split("/").slice(0, -1);
  const otherParents = others.map((other) => other.path.split("/").slice(0, -1));
  for (let count = 2; count < parents.length; count += 1) {
    const suffix = parents.slice(-count).join("/");
    if (!otherParents.some((parent) => parent.slice(-count).join("/") === suffix)) return suffix;
  }
  return parents.join("/") || ".";
}

function groupFilesByName(tabs: readonly WorkspaceTab[]): Map<string, FileTab[]> {
  const filesByName = new Map<string, FileTab[]>();
  for (const tab of tabs) {
    if (tab.kind !== "file") continue;
    const name = fileBaseName(tab.path);
    const peers = filesByName.get(name);
    if (peers) peers.push(tab);
    else filesByName.set(name, [tab]);
  }
  return filesByName;
}

type TabLabel = Pick<WorkspaceTabView, "title" | "tooltip" | "icon"> &
  Partial<Pick<WorkspaceTabView, "description" | "status">>;

function describeChat(tab: ChatTab, context: TabDescribeContext): TabLabel {
  const title = tab.conversationId === null ? "New conversation" : (tab.title ?? "Conversation");
  const status = context.conversationStatuses.get(tab.conversationId ?? tab.draftId);
  const active = status === "running" || status === "waiting-approval" || status === "detached";
  return {
    title,
    tooltip: title,
    icon: { kind: "lucide", name: "MessageSquare" },
    status: active ? status : null,
  };
}

function describeFile(
  tab: FileTab,
  context: TabDescribeContext,
  filesByName: ReadonlyMap<string, FileTab[]>,
): TabLabel {
  const title = fileBaseName(tab.path);
  const directoryName = context.directories.find(
    (directory) => directory.id === tab.directoryId,
  )?.name;
  const showDiff = shouldShowFileDiff(context.onlyDiff ?? tab.diffScope !== null, tab.diffFileView);
  return {
    title,
    description: fileDescription(tab, filesByName.get(title) ?? [], directoryName),
    tooltip:
      context.directories.length > 1 && directoryName ? `${directoryName}/${tab.path}` : tab.path,
    icon: { kind: "lucide", name: showDiff ? "GitCompare" : "FileText" },
  };
}

function describeTab(
  tab: WorkspaceTab,
  context: TabDescribeContext,
  filesByName: ReadonlyMap<string, FileTab[]>,
): TabLabel {
  switch (tab.kind) {
    case "chat":
      return describeChat(tab, context);
    case "file":
      return describeFile(tab, context, filesByName);
    case "jobs":
      return { title: "Jobs", tooltip: "Jobs", icon: { kind: "lucide", name: "Clock3" } };
    case "extension": {
      const registration = context.view(tab.viewId);
      const title = tab.title ?? registration?.title ?? tab.viewId;
      return {
        title,
        tooltip: registration?.extensionName ? `${title} · ${registration.extensionName}` : title,
        icon: registration?.icon ?? { kind: "lucide", name: "Puzzle" },
      };
    }
  }
}

export function describeTabs(group: TabGroup, context: TabDescribeContext): WorkspaceTabView[] {
  if (!hasVisibleWorkspaceTabs(group.tabs)) return [];
  const filesByName = groupFilesByName(group.tabs);
  return group.tabs.map((tab) => ({
    tab,
    description: null,
    status: null,
    unsaved: context.unsavedTabIds.has(tab.id),
    ...describeTab(tab, context, filesByName),
  }));
}

type WorkspaceTabBarProps = {
  tabs: readonly WorkspaceTabView[];
  activeTabId: string | null;
  onActivate(tabId: string): void;
  onPin(tabId: string): void;
  onClose(tabIds: readonly string[]): void;
  onMove(tabId: string, index: number): void;
};

function useTabFocus({ tabs, activeTabId, onActivate, onClose }: WorkspaceTabBarProps) {
  const buttons = React.useRef(new Map<string, HTMLButtonElement>());
  React.useEffect(() => {
    if (activeTabId)
      buttons.current.get(activeTabId)?.scrollIntoView?.({ block: "nearest", inline: "nearest" });
  }, [activeTabId]);

  function keys(event: React.KeyboardEvent, index: number) {
    let next: number;
    switch (event.key) {
      case "ArrowLeft":
        next = (index + tabs.length - 1) % tabs.length;
        break;
      case "ArrowRight":
        next = (index + 1) % tabs.length;
        break;
      case "Home":
        next = 0;
        break;
      case "End":
        next = tabs.length - 1;
        break;
      case "Enter":
      case " ":
        event.preventDefault();
        onActivate(tabs[index].tab.id);
        return;
      case "Delete":
        event.preventDefault();
        onClose([tabs[index].tab.id]);
        return;
      default:
        return;
    }
    event.preventDefault();
    buttons.current.get(tabs[next].tab.id)?.focus();
  }
  function register(id: string, element: HTMLButtonElement | null) {
    if (element) buttons.current.set(id, element);
    else buttons.current.delete(id);
  }
  return { keys, register };
}

function insertionAt(event: React.DragEvent<HTMLDivElement>, tabCount: number): number | null {
  const { target, currentTarget } = event;
  if (!(target instanceof Element) || !currentTarget.contains(target)) return null;
  const tab = target.closest<HTMLElement>("[data-workspace-tab-index]");
  if (!tab) return tabCount;
  const index = Number(tab.dataset.workspaceTabIndex);
  const { left, width } = tab.getBoundingClientRect();
  return event.clientX > left + width / 2 ? index + 1 : index;
}

function useTabDrag({ tabs, onPin, onMove }: WorkspaceTabBarProps) {
  const draggedId = React.useRef<string | null>(null);
  const [insertionIndex, setInsertionIndex] = React.useState<number | null>(null);

  function end() {
    draggedId.current = null;
    setInsertionIndex(null);
  }
  function start(event: React.DragEvent<HTMLButtonElement>, tab: WorkspaceTab, index: number) {
    draggedId.current = tab.id;
    event.dataTransfer.effectAllowed = "move";
    event.dataTransfer.setData("text/plain", tab.id);
    setInsertionIndex(index);
    if (tab.preview) onPin(tab.id);
  }
  function over(event: React.DragEvent<HTMLDivElement>) {
    if (!draggedId.current) return;
    const index = insertionAt(event, tabs.length);
    if (index === null) return;
    event.preventDefault();
    event.dataTransfer.dropEffect = "move";
    setInsertionIndex(index);
  }
  function leave(event: React.DragEvent<HTMLDivElement>) {
    const { currentTarget, relatedTarget } = event;
    if (relatedTarget instanceof Node && currentTarget.contains(relatedTarget)) return;
    setInsertionIndex(null);
  }
  function drop(event: React.DragEvent<HTMLDivElement>) {
    const id = draggedId.current;
    if (!id) return;
    const insertion = insertionAt(event, tabs.length);
    if (insertion === null) return;
    event.preventDefault();
    const source = tabs.findIndex((item) => item.tab.id === id);
    end();
    if (source >= 0) onMove(id, insertion > source ? insertion - 1 : insertion);
  }
  return { insertionIndex, start, end, over, leave, drop };
}

type TabItemProps = {
  bar: WorkspaceTabBarProps;
  view: WorkspaceTabView;
  index: number;
  drag: ReturnType<typeof useTabDrag>;
  focus: ReturnType<typeof useTabFocus>;
};

function WorkspaceTabLabel({ bar, view, index, drag, focus }: TabItemProps) {
  const { tab, title, description, unsaved, icon, status } = view;
  const active = bar.activeTabId === tab.id;
  return (
    <button
      ref={(element) => focus.register(tab.id, element)}
      type="button"
      role="tab"
      className={cn("agw-workspace-tab-label", tab.preview && "is-preview")}
      id={`workspace-tab-${tab.id}`}
      data-tab-id={tab.id}
      data-tab-kind={tab.kind}
      aria-label={description ? `${title} ${description}` : title}
      aria-selected={active}
      aria-controls={`workspace-tab-panel-${tab.id}`}
      aria-description={unsaved ? "Unsaved changes" : tab.preview ? "Preview" : undefined}
      tabIndex={active || (bar.activeTabId === null && index === 0) ? 0 : -1}
      draggable
      onClick={() => bar.onActivate(tab.id)}
      onDoubleClick={() => bar.onPin(tab.id)}
      onKeyDown={(event) => focus.keys(event, index)}
      onDragStart={(event) => drag.start(event, tab, index)}
    >
      <WorkspaceIconView icon={icon} className="shrink-0" />
      <span className="agw-workspace-tab-title truncate">{title}</span>
      {description ? (
        <span className="truncate text-xs text-muted-foreground">{description}</span>
      ) : null}
      {status ? <StatusDot status={status} /> : null}
    </button>
  );
}

function WorkspaceTabMenu({ bar, view, index }: Pick<TabItemProps, "bar" | "view" | "index">) {
  const { tab } = view;
  const ids = bar.tabs.map((item) => item.tab.id);
  return (
    <ContextMenuContent>
      {tab.preview ? (
        <>
          <ContextMenuItem onSelect={() => bar.onPin(tab.id)}>Keep Open</ContextMenuItem>
          <ContextMenuSeparator />
        </>
      ) : null}
      <ContextMenuItem onSelect={() => bar.onClose([tab.id])}>Close</ContextMenuItem>
      <ContextMenuItem
        disabled={ids.length === 1}
        onSelect={() => bar.onClose(ids.filter((id) => id !== tab.id))}
      >
        Close Others
      </ContextMenuItem>
      <ContextMenuItem
        disabled={index === ids.length - 1}
        onSelect={() => bar.onClose(ids.slice(index + 1))}
      >
        Close to the Right
      </ContextMenuItem>
      <ContextMenuItem onSelect={() => bar.onClose(ids)}>Close All</ContextMenuItem>
    </ContextMenuContent>
  );
}

function WorkspaceTabItem(props: TabItemProps) {
  const { bar, view, index, drag } = props;
  const { tab, title, tooltip, unsaved } = view;
  const last = index === bar.tabs.length - 1;
  const close = () => bar.onClose([tab.id]);
  return (
    <ContextMenu>
      <ContextMenuTrigger asChild>
        <div
          className={cn(
            "agw-workspace-tab",
            bar.activeTabId === tab.id && "is-active",
            unsaved && "is-unsaved",
            drag.insertionIndex === index && "insert-before",
            last && drag.insertionIndex === bar.tabs.length && "insert-after",
          )}
          data-workspace-tab-index={index}
          title={tooltip}
          onAuxClick={(event) => {
            if (event.button !== 1) return;
            event.preventDefault();
            close();
          }}
        >
          <WorkspaceTabLabel {...props} />
          <button
            type="button"
            className="agw-workspace-tab-close cursor-pointer"
            aria-label={`Close ${title}`}
            tabIndex={-1}
            onClick={close}
          >
            <X aria-hidden="true" />
            {unsaved ? <span className="agw-workspace-tab-unsaved" aria-hidden="true" /> : null}
          </button>
        </div>
      </ContextMenuTrigger>
      <WorkspaceTabMenu bar={bar} view={view} index={index} />
    </ContextMenu>
  );
}

export function WorkspaceTabBar(props: WorkspaceTabBarProps) {
  const listRef = React.useRef<HTMLDivElement>(null);
  useHorizontalTabWheel(listRef);
  const focus = useTabFocus(props);
  const drag = useTabDrag(props);
  return (
    <div
      ref={listRef}
      className="agw-workspace-tabs"
      role="tablist"
      aria-label="Open tabs"
      onDragOver={drag.over}
      onDragLeave={drag.leave}
      onDrop={drag.drop}
      onDragEnd={drag.end}
    >
      {props.tabs.map((view, index) => (
        <WorkspaceTabItem
          key={view.tab.id}
          bar={props}
          view={view}
          index={index}
          drag={drag}
          focus={focus}
        />
      ))}
    </div>
  );
}
