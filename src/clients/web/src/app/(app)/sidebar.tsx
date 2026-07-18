"use client";

import React from "react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { ChevronRight } from "lucide-react";

import {
  AgwLogo,
  Sidebar,
  SidebarContent,
  SidebarFooter,
  SidebarGroup,
  SidebarGroupLabel,
  SidebarHeader,
  SidebarInset,
  SidebarMenu,
  SidebarMenuButton,
  SidebarMenuItem,
  SidebarMenuSub,
  SidebarMenuSubButton,
  SidebarMenuSubItem,
  SidebarTrigger,
  SidebarRail,
} from "@agw/components";
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from "@agw/components";
import { cn } from "@agw/components";

export type MenuLink = { title: string; url: string; icon?: React.ReactNode };

export type MenuItem = {
  title: string;
  url: string;
  icon?: React.ReactNode;
  isActive: boolean;
  subMenuItems?: MenuLink[];
};

export type SidebarMenuGroupProps = {
  groupLable?: string | null;
  menus: MenuItem[];
};

type AppSidebarProps = { menus: SidebarMenuGroupProps[] };

const normalizeHref = (href: string) => href.replace(/\/$/, "");

const isActive = (pathname: string | null, href: string) =>
  pathname ? normalizeHref(href) === pathname : false;

export function AppSidebar({ menus }: AppSidebarProps) {
  const pathname = usePathname();

  return (
    <>
      <Sidebar collapsible="icon" className="relative w-54 h-full">
        <SidebarHeader className="border-b border-slate-200">
          <SidebarMenu>
            <SidebarMenuItem>
              <div className="flex items-center justify-between group-data-[collapsible=icon]:justify-center">
                <div className="mr-3 flex min-w-0 w-full items-center justify-between group-data-[collapsible=icon]:hidden">
                  <AgwLogo
                    markClassName="size-8"
                    labelClassName="truncate text-sm font-semibold tracking-tight"
                  />
                </div>

                <SidebarTrigger className="-ml-1 group-data-[collapsible=icon]:mx-auto" />
              </div>
            </SidebarMenuItem>
          </SidebarMenu>
        </SidebarHeader>
        <SidebarContent>
          {menus.map((grpItem, index) => (
            <SidebarGroup key={index}>
              {grpItem.groupLable && (
                <SidebarGroupLabel className="uppercase tracking-wider">
                  {grpItem.groupLable}
                </SidebarGroupLabel>
              )}
              <SidebarMenu>
                {grpItem.menus.map((item) => (
                  <Collapsible
                    key={item.title}
                    asChild
                    defaultOpen={item.isActive}
                    className="group/collapsible"
                  >
                    <SidebarMenuItem>
                      {!item.subMenuItems?.length ? (
                        <SidebarMenuButton
                          asChild
                          tooltip={item.title}
                          className={cn(
                            "cursor-pointer",
                            isActive(pathname, item.url)
                              ? "font-bold"
                              : "text-black/70 hover:text-foreground", // text-muted-foreground
                          )}
                        >
                          <Link href={item.url}>
                            {item.icon}
                            <span>{item.title}</span>
                          </Link>
                        </SidebarMenuButton>
                      ) : (
                        <>
                          <CollapsibleTrigger asChild>
                            <SidebarMenuButton tooltip={item.title}>
                              {item.icon}
                              <span>{item.title}</span>
                              <ChevronRight className="ml-auto transition-transform duration-200 group-data-[state=open]/collapsible:rotate-90" />
                            </SidebarMenuButton>
                          </CollapsibleTrigger>
                          <CollapsibleContent>
                            <SidebarMenuSub>
                              {item.subMenuItems.map((subItem) => (
                                <SidebarMenuSubItem key={subItem.title}>
                                  <SidebarMenuSubButton
                                    asChild
                                    className={`cursor-pointer ${isActive(pathname, subItem.url) ? "font-bold" : ""}`}
                                  >
                                    <Link href={subItem.url}>
                                      {subItem.icon}
                                      <span>{subItem.title}</span>
                                    </Link>
                                  </SidebarMenuSubButton>
                                </SidebarMenuSubItem>
                              ))}
                            </SidebarMenuSub>
                          </CollapsibleContent>
                        </>
                      )}
                    </SidebarMenuItem>
                  </Collapsible>
                ))}
              </SidebarMenu>
            </SidebarGroup>
          ))}
        </SidebarContent>
        <SidebarFooter />
        <SidebarRail />
      </Sidebar>
    </>
  );
}

// Re-export SidebarInset for use in layouts
export { SidebarInset };
