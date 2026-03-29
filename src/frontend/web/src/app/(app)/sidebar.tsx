"use client";

import React from "react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { ChevronRight } from "lucide-react";

import {
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
} from "@/components/ui/sidebar";
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from "@/components/ui/collapsible";

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
              <SidebarMenuButton className="flex-row-reverse justify-between">
                <SidebarTrigger className="-ml-1" />
                <span>Agw</span>
                {/* <span>Squidward</span> */}
              </SidebarMenuButton>
            </SidebarMenuItem>
          </SidebarMenu>
        </SidebarHeader>
        <SidebarContent>
          {menus.map((grpItem, index) => (
            <SidebarGroup key={index}>
              {grpItem.groupLable && <SidebarGroupLabel>{grpItem.groupLable}</SidebarGroupLabel>}
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
                          className={`cursor-pointer ${isActive(pathname, item.url) ? "font-bold" : ""}`}
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
