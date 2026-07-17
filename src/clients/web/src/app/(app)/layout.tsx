"use client";

import * as React from "react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import {
  Workflow,
  Bot,
  Blocks,
  // Terminal,
  Boxes,
  Cable,
  MessagesSquare,
  Clock,
  Gauge,
  Settings,
  Box,
  FolderKanban,
  Network,
  // Waypoints,
  // Hammer,
} from "lucide-react";

import {
  Breadcrumb,
  BreadcrumbItem,
  BreadcrumbLink,
  BreadcrumbList,
  BreadcrumbPage,
  BreadcrumbSeparator,
} from "@/components/ui/breadcrumb";
import { AppShell, useDesktopRuntime } from "@/features/desktop";
import { QueryErrorBoundary } from "@/components/query-error-boundary";
import { AppSidebar, MenuItem, SidebarMenuGroupProps } from "./sidebar";
import { SidebarProvider } from "@/components/ui/sidebar";
import { AuthGate } from "@/components/auth-gate";

const navItems: SidebarMenuGroupProps[] = [
  {
    groupLable: "Overview",
    menus: [
      {
        url: "/dashboard",
        title: "Dashboard",
        isActive: true,
        icon: <Gauge />,
      },
      // {
      //   url: "/traces",
      //   title: "Traces",
      //   isActive: true,
      //   icon: <Waypoints />,
      // },
    ],
  },
  {
    groupLable: "Projects",
    menus: [
      {
        url: "/chat",
        title: "Chat",
        isActive: true,
        icon: <MessagesSquare />,
      },
      {
        url: "/projects",
        title: "Projects",
        isActive: true,
        icon: <FolderKanban />,
      },
      {
        url: "/jobs",
        title: "Jobs",
        isActive: true,
        icon: <Clock />,
      },
    ],
  },

  {
    groupLable: "Agent & Flow",
    menus: [
      {
        url: "/agents",
        title: "Agents",
        isActive: true,
        icon: <Bot />,
      },
      {
        url: "/agentflows",
        title: "Agentflows",
        isActive: true,
        icon: <Workflow />,
      },
      {
        url: "/mcp-tool-servers",
        title: "MCP Tool Servers",
        isActive: true,
        icon: <Network />,
      },
      {
        url: "/skills",
        title: "Skills",
        isActive: true,
        icon: <Blocks />,
      },
    ],
  },

  {
    groupLable: "Model & Provider",
    menus: [
      {
        url: "/providers",
        title: "Providers",
        isActive: true,
        icon: <Boxes />,
      },
      {
        url: "/models",
        title: "Models",
        isActive: true,
        // icon: <Blocks />,
        icon: <Box />,
      },
    ],
  },

  {
    groupLable: "Integrations",
    menus: [
      {
        url: "/integrations",
        title: "Integrations (beta)",
        isActive: true,
        icon: <Cable />,
      },
    ],
  },

  {
    groupLable: "System",
    menus: [
      {
        url: "/settings",
        title: "Settings",
        isActive: true,
        icon: <Settings />,
      },
    ],
  },
];

function getActiveNavLabel(pathname: string): MenuItem | undefined {
  const allNavItems = navItems.flatMap((group) => group.menus);
  const match = allNavItems.find((x) => pathname === x.url || pathname.startsWith(`${x.url}/`));
  return match;
}

export default function AppLayout({ children }: { children: React.ReactNode }) {
  const pathname = usePathname();
  const desktop = useDesktopRuntime();
  const activeMenu = getActiveNavLabel(pathname);
  const [sidebarOpen, setSidebarOpen] = React.useState(pathname !== "/chat");

  React.useEffect(() => {
    if (pathname === "/chat") {
      setSidebarOpen(false);
    }
  }, [pathname]);

  if (desktop.isDesktop) {
    return (
      <AuthGate>
        <AppShell>
          <QueryErrorBoundary>{children}</QueryErrorBoundary>
        </AppShell>
      </AuthGate>
    );
  }

  return (
    <AuthGate>
      <SidebarProvider
        open={sidebarOpen}
        onOpenChange={setSidebarOpen}
        style={
          {
            "--sidebar-width": "14rem",
          } as React.CSSProperties
        }
      >
        <div className="min-h-screen bg-background text-foreground w-full">
          {/* <header className="flex h-16 border-b ">
          <div className="flex items-center px-6 w-64">
            <Link href="/projects" className="font-semibold tracking-tight">
              Agw Admin
            </Link>
            <SidebarTrigger className="-ml-1 md:hidden" />
          </div>
        </header> */}

          <div className="flex min-h-screen w-full overflow-x-hidden">
            <aside className="flex min-h-[calc(100vh-64px)]">
              <AppSidebar menus={navItems} />
            </aside>

            <div className="px-2 flex-1 min-w-0 max-w-full flex flex-col overflow-x-hidden">
              <div className="sticky top-0 z-40 flex items-center gap-3 bg-background/80 backdrop-blur supports-backdrop-filter:bg-background/60 py-2">
                <div className="min-w-0 flex-1">
                  <Breadcrumb>
                    <BreadcrumbList>
                      <BreadcrumbItem>
                        <BreadcrumbLink asChild>
                          <Link href="/projects">Home</Link>
                        </BreadcrumbLink>
                      </BreadcrumbItem>
                      {activeMenu ? (
                        <>
                          <BreadcrumbSeparator />
                          <BreadcrumbItem>
                            <BreadcrumbPage>{activeMenu.title}</BreadcrumbPage>
                          </BreadcrumbItem>
                        </>
                      ) : null}
                    </BreadcrumbList>
                  </Breadcrumb>
                </div>
              </div>

              <main className="flex min-w-0 max-w-full flex-1 justify-center overflow-x-hidden">
                <QueryErrorBoundary>{children}</QueryErrorBoundary>
              </main>
            </div>
          </div>
        </div>
      </SidebarProvider>
    </AuthGate>
  );
}
