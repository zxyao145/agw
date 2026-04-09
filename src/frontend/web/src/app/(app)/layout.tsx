"use client";

import * as React from "react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import {
  LayoutDashboard,
  Workflow,
  Bot,
  Package,
  Blocks,
  // Terminal,
  Server,
  Boxes,
  Box,
  // Gauge,
  // Waypoints,
  // Link2,
  Cable,
  MessagesSquare,
  Clock,
  Hammer,
} from "lucide-react";

import {
  Breadcrumb,
  BreadcrumbItem,
  BreadcrumbLink,
  BreadcrumbList,
  BreadcrumbPage,
  BreadcrumbSeparator,
} from "@/components/ui/breadcrumb";
import { QueryErrorBoundary } from "@/components/query-error-boundary";
import { AppSidebar, MenuItem, SidebarMenuGroupProps } from "./sidebar";
import { SidebarProvider } from "@/components/ui/sidebar";

const navItems: SidebarMenuGroupProps[] = [
  // {
  //   groupLable: "Overview",
  //   menus: [
  //     {
  //       url: "/dashboard",
  //       title: "Dashboard",
  //       isActive: true,
  //       icon: <Gauge />,
  //     },
  //     {
  //       url: "/traces",
  //       title: "Traces",
  //       isActive: true,
  //       icon: <Waypoints />,
  //     },
  //   ],
  // },

  {
    groupLable: "Projects",
    menus: [
      {
        url: "/chat",
        title: "chat",
        isActive: true,
        icon: <MessagesSquare />,
      },
      {
        url: "/projects",
        title: "Projects",
        isActive: true,
        icon: <LayoutDashboard />,
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
        icon: <Server />,
      },
      {
        url: "/skills",
        title: "Skills",
        isActive: true,
        icon: <Package />,
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
        icon: <Blocks />,
      },
      {
        url: "/model-providers",
        title: "Model Providers",
        isActive: true,
        icon: <Box />,
      },
    ],
  },

  {
    groupLable: "Integrations",
    menus: [
      {
        url: "/integrations",
        title: "Integrations",
        isActive: true,
        icon: <Cable />,
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
  const activeMenu = getActiveNavLabel(pathname);
  const [sidebarOpen, setSidebarOpen] = React.useState(pathname !== "/claude-code");

  React.useEffect(() => {
    if (pathname === "/claude-code") {
      setSidebarOpen(false);
    }
  }, [pathname]);

  return (
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
  );
}
