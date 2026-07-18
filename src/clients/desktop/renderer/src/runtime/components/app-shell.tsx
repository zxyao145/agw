"use client";

import * as React from "react";
import Link from "next/link";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { useQuery } from "@agw/components/query";
import {
  ArrowLeft,
  Blocks,
  Bot,
  Boxes,
  Cable,
  Check,
  ChevronDown,
  Clock3,
  Cloud,
  FolderKanban,
  Gauge,
  GitBranch,
  Info,
  KeyRound,
  LoaderCircle,
  Moon,
  Network,
  Server,
  Settings,
  Sparkles,
  Sun,
  Workflow,
  X,
} from "lucide-react";
import { useTheme } from "next-themes";

import { apiGet } from "@agw/api";
import { getApiErrorMessage } from "@agw/api";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@agw/components";
import { Popover, PopoverContent, PopoverTrigger } from "@agw/components";
import { useExecutionActivity } from "@agw/chat";
import type { ExecutionStatus } from "@agw/chat";
import { buildChatHref } from "@agw/chat";
import { DEFAULT_PROJECT_ID, normalizeProjectTabs } from "@agw/projects";
import { cn } from "@agw/components";
import { useDesktopRuntime } from "../runtime-provider";
import { DesktopProjectPicker, type DesktopProjectOption } from "./project-picker";

type ProjectSummary = DesktopProjectOption;

const CHAT_PATHS = new Set(["/chat", "/desktop/chat"]);

const SETTINGS_GROUPS = [
  {
    label: "Operations",
    items: [
      { href: "/dashboard/", label: "Overview", icon: Gauge },
      { href: "/jobs/", label: "Jobs", icon: Clock3 },
    ],
  },
  {
    label: "Workspace",
    items: [
      { href: "/projects/", label: "Projects", icon: FolderKanban },
      // { href: "/projects/conversations/details/", label: "Conversations", icon: MessagesSquare },
    ],
  },
  {
    label: "AI runtime",
    items: [
      { href: "/agents/", label: "Agents", icon: Bot },
      { href: "/agentflows/", label: "Agentflows", icon: Workflow },
      { href: "/providers/", label: "Providers", icon: Boxes },
      { href: "/models/", label: "Models", icon: Sparkles },
    ],
  },
  {
    label: "Capabilities",
    items: [
      // { href: "/skills/", label: "Skills", icon: GitBranch },
      { href: "/skills/", label: "Skills", icon: Blocks },
      { href: "/mcp-tool-servers/", label: "MCP servers", icon: Network },
      { href: "/integrations/", label: "Integrations", icon: Cable },
    ],
  },
  {
    label: "Desktop & Server",
    items: [
      { href: "/settings/", label: "Connections & app", icon: Server },
      // { href: "/settings/#local-server", label: "Local server", icon: KeyRound },
      { href: "/settings/#appearance", label: "Appearance & close", icon: Moon },
      { href: "/settings/#about", label: "About", icon: Info },
    ],
  },
] as const;

const STATUS_LABEL: Record<ExecutionStatus, string> = {
  idle: "Idle",
  running: "Running",
  "waiting-approval": "Waiting for approval",
  "completed-unread": "Completed",
  "failed-unread": "Failed",
  detached: "Detached",
};

function ThemeButton() {
  const { resolvedTheme, setTheme } = useTheme();
  const isDark = resolvedTheme === "dark";
  return (
    <button
      type="button"
      className="agw-titlebar-button"
      aria-label={isDark ? "Use light theme" : "Use dark theme"}
      onClick={() => setTheme(isDark ? "light" : "dark")}
    >
      {isDark ? <Sun /> : <Moon />}
    </button>
  );
}

function AppMark({ label }: { label: string }) {
  return (
    <span className="agw-brand">
      <span className="agw-logo">A</span>
      <span>{label}</span>
    </span>
  );
}

function StatusDot({ status }: { status: ExecutionStatus }) {
  return (
    <span className={cn("agw-status-dot", `is-${status}`)} aria-label={STATUS_LABEL[status]} />
  );
}

function ChatShell({ children }: { children: React.ReactNode }) {
  const router = useRouter();
  const searchParams = useSearchParams();
  const desktop = useDesktopRuntime();
  const activity = useExecutionActivity();
  const [serverPickerOpen, setServerPickerOpen] = React.useState(false);
  const activeProjectId = searchParams.get("projectId") ?? DEFAULT_PROJECT_ID;
  const serverId = desktop.activeProfile?.id ?? "browser";
  const runtimeSettings = desktop.runtimeState?.settings;
  const serverProfiles = runtimeSettings?.profiles ?? [];
  const activeServerId = runtimeSettings?.activeServerId ?? serverId;
  const projectsQuery = useQuery({
    queryKey: ["projects", serverId],
    queryFn: async () => (await apiGet("/api/projects")) as ProjectSummary[],
  });
  const projects = projectsQuery.data ?? [];
  const loadedProjectIds = projects.map((project) => project.id);
  const storedTabs = desktop.runtimeState?.settings.projectTabsByServer[serverId] ?? [];
  const [browserTabs, setBrowserTabs] = React.useState<string[]>([DEFAULT_PROJECT_ID]);

  React.useEffect(() => {
    if (desktop.isDesktop) return;
    try {
      const stored = JSON.parse(localStorage.getItem("agw.project-tabs") ?? "[]") as string[];
      setBrowserTabs(stored);
    } catch {
      setBrowserTabs([DEFAULT_PROJECT_ID]);
    }
  }, [desktop.isDesktop]);

  const projectIds = projectsQuery.data
    ? loadedProjectIds
    : [...new Set([...storedTabs, ...browserTabs, activeProjectId])];

  const tabs = React.useMemo(
    () =>
      normalizeProjectTabs(
        desktop.isDesktop ? storedTabs : browserTabs,
        projectIds,
        activeProjectId,
      ),
    [activeProjectId, browserTabs, desktop.isDesktop, projectIds, storedTabs],
  );

  const persistTabs = React.useCallback(
    (nextTabs: string[]) => {
      if (desktop.isDesktop && desktop.runtimeState) {
        void desktop.saveSettings({
          ...desktop.runtimeState.settings,
          projectTabsByServer: {
            ...desktop.runtimeState.settings.projectTabsByServer,
            [serverId]: nextTabs,
          },
        });
      } else {
        setBrowserTabs(nextTabs);
        localStorage.setItem("agw.project-tabs", JSON.stringify(nextTabs));
      }
    },
    [desktop, serverId],
  );

  React.useEffect(() => {
    const source = desktop.isDesktop ? storedTabs : browserTabs;
    if (projects.length > 0 && JSON.stringify(source) !== JSON.stringify(tabs)) persistTabs(tabs);
  }, [browserTabs, desktop.isDesktop, persistTabs, projects.length, storedTabs, tabs]);

  const projectById = React.useMemo(
    () => new Map(projects.map((project) => [project.id, project])),
    [projects],
  );

  const closeTab = (projectId: string) => {
    const status = activity.getProjectStatus(projectId);
    if (
      ["running", "waiting-approval", "detached"].includes(status) &&
      !window.confirm("This Project has a task running in the background. Close its tab anyway?")
    ) {
      return;
    }
    const nextTabs = tabs.filter((id) => id !== projectId);
    persistTabs(nextTabs);
    if (activeProjectId === projectId) {
      router.push(
        buildChatHref("/desktop/chat", { projectId: DEFAULT_PROJECT_ID, contextId: null }),
      );
    }
  };

  const openProject = (projectId: string) => {
    const nextTabs = normalizeProjectTabs(tabs, projectIds, projectId);
    persistTabs(nextTabs);
    router.push(buildChatHref("/desktop/chat", { projectId, contextId: null }));
  };

  const switchServer = (nextServerId: string) => {
    setServerPickerOpen(false);
    if (!runtimeSettings || nextServerId === runtimeSettings.activeServerId) return;
    router.replace(
      buildChatHref("/desktop/chat", { projectId: DEFAULT_PROJECT_ID, contextId: null }),
    );
    void desktop.saveSettings({ ...runtimeSettings, activeServerId: nextServerId });
  };

  const platform = desktop.runtimeState?.platform ?? "browser";
  const hasBackgroundConversations = tabs.some((id) => activity.getProjectStatus(id) !== "idle");
  return (
    <div className="agw-app-shell">
      <header className={cn("agw-titlebar", `platform-${platform}`)}>
        <span className="ml-4"></span>
        {/* <Link href="/desktop/chat/" className="agw-titlebar-control">
          <AppMark label="Agw Chat" />
        </Link> */}
        <nav className="agw-project-tabs" aria-label="Open projects">
          <div className="flex min-w-0 max-w-full items-center gap-1 overflow-hidden">
            {tabs.map((projectId) => {
              const status = activity.getProjectStatus(projectId);
              return (
                <div
                  key={projectId}
                  className={cn("agw-project-tab", activeProjectId === projectId && "is-active")}
                >
                  <Link href={buildChatHref("/desktop/chat", { projectId, contextId: null })}>
                    <StatusDot status={status} />
                    <span>{projectById.get(projectId)?.name ?? projectId}</span>
                  </Link>
                  {projectId !== DEFAULT_PROJECT_ID ? (
                    <button
                      type="button"
                      className="cursor-pointer"
                      aria-label={`Close ${projectId}`}
                      onClick={() => closeTab(projectId)}
                    >
                      <X />
                    </button>
                  ) : null}
                </div>
              );
            })}
            <DesktopProjectPicker
              projects={projects}
              activeProjectId={activeProjectId}
              isLoading={projectsQuery.isLoading}
              errorMessage={projectsQuery.isError ? getApiErrorMessage(projectsQuery.error) : null}
              onSelect={openProject}
            />
          </div>
        </nav>

        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <button type="button" className="agw-task-button agw-titlebar-control">
              <LoaderCircle className={cn(hasBackgroundConversations && "animate-spin")} />
              <span>Conversations</span>
              {activity.activeCount > 0 ? <span>{activity.activeCount}</span> : null}
            </button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end" className="w-64">
            <DropdownMenuLabel>Background conversations</DropdownMenuLabel>
            <DropdownMenuSeparator />
            {hasBackgroundConversations ? (
              tabs
                .filter((id) => activity.getProjectStatus(id) !== "idle")
                .map((id) => (
                  <DropdownMenuItem key={id} asChild>
                    <Link href={buildChatHref("/desktop/chat", { projectId: id, contextId: null })}>
                      <StatusDot status={activity.getProjectStatus(id)} />
                      <span className="flex-1 truncate">{projectById.get(id)?.name ?? id}</span>
                      <span className="text-xs text-muted-foreground">
                        {STATUS_LABEL[activity.getProjectStatus(id)]}
                      </span>
                    </Link>
                  </DropdownMenuItem>
                ))
            ) : (
              <DropdownMenuItem disabled>No background conversations</DropdownMenuItem>
            )}
          </DropdownMenuContent>
        </DropdownMenu>

        <Popover open={serverPickerOpen} onOpenChange={setServerPickerOpen}>
          <PopoverTrigger asChild>
            <button
              type="button"
              className="agw-server-pill agw-titlebar-control"
              aria-label="Switch server"
            >
              <span className={cn("agw-server-state", desktop.status === "ready" && "is-online")} />
              <span className="max-w-32 truncate">
                {desktop.activeProfile?.name ?? "Localhost"}
              </span>
              <ChevronDown className="size-3 text-muted-foreground" />
            </button>
          </PopoverTrigger>
          <PopoverContent
            align="end"
            sideOffset={8}
            className="w-80 rounded-2xl border-border/70 bg-popover/98 p-0 shadow-2xl shadow-black/12 backdrop-blur-xl"
          >
            <div className="border-b border-border/70 px-4 py-3">
              <div className="text-sm font-semibold tracking-tight">Switch server</div>
              <div className="mt-0.5 text-xs text-muted-foreground">
                Choose where Desktop connects
              </div>
            </div>
            <div className="max-h-80 overflow-y-auto p-2.5" role="listbox" aria-label="Servers">
              {serverProfiles.map((profile) => {
                const active = profile.id === activeServerId;
                const ProfileIcon = profile.kind === "remote" ? Cloud : Server;
                return (
                  <button
                    key={profile.id}
                    type="button"
                    role="option"
                    aria-selected={active}
                    className={cn(
                      "cursor-pointer group flex w-full items-center gap-3 rounded-xl px-3 py-2.5 text-left outline-none transition-colors",
                      "hover:bg-muted/70 focus-visible:bg-muted focus-visible:ring-2 focus-visible:ring-ring/40",
                      active && "bg-primary/8 text-foreground",
                    )}
                    onClick={() => switchServer(profile.id)}
                  >
                    <span
                      className={cn(
                        "grid size-8 shrink-0 place-items-center rounded-[10px] border bg-background text-muted-foreground",
                        active && "border-primary/25 bg-primary/10 text-primary",
                      )}
                    >
                      <ProfileIcon className="size-4" />
                    </span>
                    <span className="min-w-0 flex-1">
                      <span className="block truncate text-sm font-medium">{profile.name}</span>
                      <span className="mt-0.5 block truncate text-xs text-muted-foreground">
                        {profile.baseUrl}
                      </span>
                    </span>
                    <span className="grid size-5 shrink-0 place-items-center text-primary">
                      {active ? <Check className="size-4" /> : null}
                    </span>
                  </button>
                );
              })}
            </div>
          </PopoverContent>
        </Popover>
        <span className="agw-titlebar-control">
          <ThemeButton />
        </span>
        <Link
          href="/dashboard/"
          className="agw-titlebar-button agw-titlebar-control"
          aria-label="Settings"
        >
          <Settings />
        </Link>
      </header>
      <main className="agw-chat-workspace">{children}</main>
    </div>
  );
}

function SettingsShell({ children }: { children: React.ReactNode }) {
  const pathname = usePathname();
  const desktop = useDesktopRuntime();
  const platform = desktop.runtimeState?.platform ?? "browser";
  const [hash, setHash] = React.useState("");
  React.useEffect(() => {
    const updateHash = () => setHash(window.location.hash);
    updateHash();
    window.addEventListener("hashchange", updateHash);
    return () => window.removeEventListener("hashchange", updateHash);
  }, []);
  return (
    <div className="agw-app-shell">
      <header className={cn("agw-titlebar agw-settings-titlebar", `platform-${platform}`)}>
        <Link href="/desktop/chat/" className="agw-back-button agw-titlebar-control ml-4">
          <ArrowLeft />
          <span>Back to chat</span>
        </Link>
        {/* <AppMark label="Settings" /> */}
        <span className="agw-titlebar-spacer" />
        <span className="agw-titlebar-control">
          <ThemeButton />
        </span>
      </header>
      <div className="agw-settings-workspace">
        <aside className="agw-settings-nav">
          <div className="agw-settings-nav-heading">Agw</div>
          {SETTINGS_GROUPS.map((group) => (
            <section key={group.label}>
              <h2>{group.label}</h2>
              {group.items.map((item) => {
                const [rawHrefPath, hrefHash] = item.href.split("#");
                const hrefPath = rawHrefPath.replace(/\/$/u, "");
                const active =
                  hrefPath === "/settings"
                    ? pathname === "/settings" && (hrefHash ? hash === `#${hrefHash}` : !hash)
                    : pathname === hrefPath || pathname.startsWith(`${hrefPath}/`);
                const Icon = item.icon;
                return (
                  <Link key={item.href} href={item.href} className={cn(active && "is-active")}>
                    <Icon />
                    <span>{item.label}</span>
                  </Link>
                );
              })}
            </section>
          ))}
        </aside>
        <main className="agw-settings-main">{children}</main>
      </div>
    </div>
  );
}

export function AppShell({ children }: { children: React.ReactNode }) {
  const pathname = usePathname();
  const normalizedPathname = pathname.replace(/\/+$/u, "");
  return CHAT_PATHS.has(normalizedPathname) ? (
    <ChatShell>{children}</ChatShell>
  ) : (
    <SettingsShell>{children}</SettingsShell>
  );
}
