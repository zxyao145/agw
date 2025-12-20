"use client";

import React, { ReactElement, useCallback, useMemo, useState } from "react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import {
  ChevronDown,
  UserRound,
  ShieldPlus,
  Fingerprint,
  Dot,
  LayoutGrid,
  Menu,
  X,
  ChevronRight,
  LucideIcon,
} from "lucide-react";
import { Button } from "@/components/ui/button";

import { Separator } from "@/components/ui/separator";
import {
  SidebarGroup,
  SidebarGroupLabel,
  SidebarMenu,
  SidebarMenuButton,
  SidebarMenuItem,
  SidebarMenuSub,
  SidebarMenuSubButton,
  SidebarMenuSubItem,
  SidebarInset,
  SidebarProvider,
  SidebarTrigger,
  Sidebar,
  SidebarContent,
  SidebarFooter,
  SidebarHeader,
  SidebarRail,
} from "@/components/ui/sidebar";
import {
  Collapsible,
  CollapsibleContent,
  CollapsibleTrigger,
} from "@/components/ui/collapsible";

export type MenuLink = {
  title: string;
  url: string;
};

export type MenuItem = {
  title: string;
  url: string;
  icon?: React.ReactNode;
  isActive: boolean;
  subMenuItems?: MenuLink[];
};

const normalizeHref = (href: string) => href.replace(/\/$/, "");

const isActive = (pathname: string | null, href: string) => {
  if (!pathname) {
    return false;
  }
  const normalizedHref = normalizeHref(href);
  return (
    pathname === normalizedHref // || pathname.startsWith(`${normalizedHref}/`)
  );
};


export type SidebarMenuGroupProps = {
  groupLable?: string | null;
  menus: MenuItem[];
};

type AppSidebarProps = {
  menus: SidebarMenuGroupProps[];
};

export function AppSidebar( props: AppSidebarProps) {
  const pathname = usePathname();

  return (
    <>
      <Sidebar collapsible="icon" className="relative w-64 h-full">
        <SidebarHeader className="border-b border-slate-200">
          <div className="flex justify-end">
            <SidebarTrigger className="-ml-1" />
          </div>
        </SidebarHeader>
        <SidebarContent>
          {props.menus.map((grpItem, index) => (
            <SidebarGroup key={index}>
              {grpItem.groupLable && (
                <SidebarGroupLabel>{grpItem.groupLable}</SidebarGroupLabel>
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
                      {item.subMenuItems == null || item.subMenuItems.length === 0 ? (
                        <SidebarMenuSubButton
                          asChild
                          className={`${
                            isActive(pathname, item.url) ? "font-bold" : ""
                          }`}
                        >
                          <Link href={item.url}>
                            <span>{item.title}</span>
                          </Link>
                        </SidebarMenuSubButton>
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
                              {item.subMenuItems?.map((subItem) => (
                                <SidebarMenuSubItem key={subItem.title}>
                                  <SidebarMenuSubButton
                                    asChild
                                    className={`${
                                      isActive(pathname, subItem.url)
                                        ? "font-bold"
                                        : ""
                                    }`}
                                  >
                                    <Link href={subItem.url}>
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
        <SidebarFooter></SidebarFooter>
        <SidebarRail />
      </Sidebar>
    </>
  );
}
