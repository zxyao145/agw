import {
  Blocks,
  BookOpenText,
  Bot,
  Boxes,
  Cable,
  Clock,
  FolderKanban,
  Gauge,
  MessagesSquare,
  Network,
  Settings,
  Sparkle,
  Sparkles,
  Workflow,
  type LucideIcon,
} from "lucide-react";

export type AppRoute = {
  readonly href: string;
  readonly title: string;
  readonly icon: LucideIcon;
};

// Routes shared by the Web and Desktop shells. Each shell groups them and may override the
// displayed title or icon; the Desktop-only Chat route stays in the Desktop application.
// Web 与 Desktop 外壳共用的路由。各外壳自行分组，并可覆盖显示标题或图标；
// Desktop 专属的 Chat 路由留在 Desktop 应用内。
export const APP_ROUTES = {
  dashboard: { href: "/dashboard", title: "Dashboard", icon: Gauge },
  chat: { href: "/chat", title: "Chat", icon: MessagesSquare },
  projects: { href: "/projects", title: "Projects", icon: FolderKanban },
  jobs: { href: "/jobs", title: "Jobs", icon: Clock },
  agents: { href: "/agents", title: "Agents", icon: Bot },
  agentflows: { href: "/agentflows", title: "Agentflows", icon: Workflow },
  providers: { href: "/providers", title: "Providers", icon: Boxes },
  models: { href: "/models", title: "Models", icon: Sparkles },
  userMemory: { href: "/user-memory", title: "User Memory", icon: BookOpenText },
  quickPrompts: { href: "/quick-prompts", title: "Quick prompts", icon: Sparkle },
  skills: { href: "/skills", title: "Skills", icon: Blocks },
  mcpToolServers: { href: "/mcp-tool-servers", title: "MCP Tool Servers", icon: Network },
  integrations: { href: "/integrations", title: "Integrations", icon: Cable },
  settings: { href: "/settings", title: "Settings", icon: Settings },
} as const satisfies Record<string, AppRoute>;
