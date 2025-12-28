"use client";

import * as React from "react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { MenuIcon, LayoutDashboard, Workflow, Bot, Cog, Package, Blocks, Terminal } from "lucide-react";

import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import {
  Breadcrumb,
  BreadcrumbItem,
  BreadcrumbLink,
  BreadcrumbList,
  BreadcrumbPage,
  BreadcrumbSeparator,
} from "@/components/ui/breadcrumb";
import { Separator } from "@/components/ui/separator";
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetHeader,
  SheetTitle,
  SheetTrigger,
} from "@/components/ui/sheet";
import { QueryErrorBoundary } from "@/components/query-error-boundary";
import { AppSidebar, MenuItem, SidebarMenuGroupProps } from "./sidebar";
import { SidebarProvider, SidebarTrigger } from "@/components/ui/sidebar";
import { group } from "console";

const navItems: SidebarMenuGroupProps[] = [
  {
    groupLable: "Projects",
    menus: [
      {
        url: "/projects",
        title: "Projects",
        isActive: true,
        icon: <LayoutDashboard />,
      },
    ],
  },
  {
    groupLable: "Agent & Flow",
    menus: [
      {
        url: "/agentflows",
        title: "Agentflows",
        isActive: true,
        icon: <Workflow />
      },
      {
        url: "/agents",
        title: "Agents",
        isActive: true,
        icon: <Bot />
      },
      {
        url: "/claude-code",
        title: "ClaudeCode",
        isActive: true,
        icon: <Terminal />
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
        icon: <Cog />
      },
      {
        url: "/models",
        title: "Models",
        isActive: true,
        icon: <Blocks />
      },
      {
        url: "/model-providers",
        title: "Model Providers",
        isActive: true,
        icon: <Package />
      },
    ],
  },
];

function getActiveNavLabel(pathname: string): MenuItem | undefined {
  const allNavItems = navItems.flatMap((group) => group.menus);
  const match = allNavItems.find(
    (x) => pathname === x.url || pathname.startsWith(`${x.url}/`)
  );
  return match;
}

export default function AppLayout({ children }: { children: React.ReactNode }) {
  const pathname = usePathname();
  const activeMenu = getActiveNavLabel(pathname);

  return (
    <SidebarProvider>
      <div className="min-h-screen bg-background text-foreground w-full">
        <header className="flex h-16 border-b ">
          <div className="flex items-center px-6 w-64">
            <Link href="/projects" className="font-semibold tracking-tight">
              DSystem Admin
            </Link>
            <SidebarTrigger className="-ml-1 md:hidden" />
          </div>
        </header>

        <div className="grid grid-cols-[max-content_1fr] gap-4 min-h-screen w-full">
          <aside className="flex min-h-[calc(100vh-64px)]">
            <AppSidebar menus={navItems} />
          </aside>

          <div className="p-2 flex-1">
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

            <main className="mt-4 flex justify-center">
              <QueryErrorBoundary>{children}</QueryErrorBoundary>
            </main>
          </div>
        </div>
      </div>
    </SidebarProvider>
  );
}
