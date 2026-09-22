"use client";

import * as React from "react";
import Link from "next/link";
import { usePathname } from "next/navigation";

import {
  APP_ROUTES,
  Breadcrumb,
  BreadcrumbItem,
  BreadcrumbLink,
  BreadcrumbList,
  BreadcrumbPage,
  BreadcrumbSeparator,
  cn,
  type AppRoute,
} from "@agw/components";
import { QueryErrorBoundary } from "@agw/components";
import { AppSidebar, MenuItem, SidebarMenuGroupProps } from "./sidebar";
import { SidebarProvider } from "@agw/components";
import { AuthGate } from "@agw/auth";

function menuItem(route: AppRoute): MenuItem {
  return { url: route.href, title: route.title, isActive: true, icon: <route.icon /> };
}

const navItems: SidebarMenuGroupProps[] = [
  { groupLable: "Overview", menus: [menuItem(APP_ROUTES.dashboard)] },
  {
    groupLable: "Projects",
    menus: [APP_ROUTES.chat, APP_ROUTES.projects, APP_ROUTES.jobs].map(menuItem),
  },
  { groupLable: "Agent & Flow", menus: [APP_ROUTES.agents, APP_ROUTES.agentflows].map(menuItem) },
  {
    groupLable: "Model & Provider",
    menus: [APP_ROUTES.providers, APP_ROUTES.models].map(menuItem),
  },
  {
    groupLable: "Capabilities",
    menus: [
      APP_ROUTES.userMemory,
      APP_ROUTES.quickPrompts,
      APP_ROUTES.skills,
      APP_ROUTES.mcpToolServers,
      APP_ROUTES.integrations,
    ].map(menuItem),
  },
  { groupLable: "System", menus: [menuItem(APP_ROUTES.settings)] },
];

function getActiveNavLabel(pathname: string): MenuItem | undefined {
  const allNavItems = navItems.flatMap((group) => group.menus);
  const match = allNavItems.find((x) => pathname === x.url || pathname.startsWith(`${x.url}/`));
  return match;
}

export default function AppLayout({ children }: { children: React.ReactNode }) {
  const pathname = usePathname();
  const isChatRoute = pathname === "/chat";
  const activeMenu = getActiveNavLabel(pathname);
  const [sidebarOpen, setSidebarOpen] = React.useState(pathname !== "/chat");

  React.useEffect(() => {
    if (pathname === "/chat") {
      setSidebarOpen(false);
    }
  }, [pathname]);

  return (
    <AuthGate>
      <SidebarProvider
        className={cn(isChatRoute && "h-dvh overflow-hidden")}
        open={sidebarOpen}
        onOpenChange={setSidebarOpen}
        style={
          {
            "--sidebar-width": "14rem",
          } as React.CSSProperties
        }
      >
        <div
          className={cn(
            "bg-background text-foreground w-full",
            isChatRoute ? "h-dvh overflow-hidden" : "min-h-screen",
          )}
        >
          {/* <header className="flex h-16 border-b ">
          <div className="flex items-center px-6 w-64">
            <Link href="/projects" className="font-semibold tracking-tight">
              Agw Admin
            </Link>
            <SidebarTrigger className="-ml-1 md:hidden" />
          </div>
        </header> */}

          <div
            className={cn(
              "flex w-full overflow-x-hidden",
              isChatRoute ? "h-full min-h-0 overflow-y-hidden" : "min-h-screen",
            )}
          >
            <aside className="flex min-h-[calc(100vh-64px)]">
              <AppSidebar menus={navItems} />
            </aside>

            <div className="px-2 flex min-h-0 min-w-0 max-w-full flex-1 flex-col overflow-x-hidden">
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

              <main
                className={cn(
                  "flex min-h-0 min-w-0 max-w-full flex-1 justify-center",
                  isChatRoute ? "overflow-hidden" : "overflow-x-hidden",
                )}
              >
                <QueryErrorBoundary>{children}</QueryErrorBoundary>
              </main>
            </div>
          </div>
        </div>
      </SidebarProvider>
    </AuthGate>
  );
}
