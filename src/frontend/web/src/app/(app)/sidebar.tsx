"use client";

import React from "react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { ChevronRight, Share2 } from "lucide-react";

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
import { Button } from "@/components/ui/button";
import { toast } from "sonner";
import { copyMobileLocalConfigToClipboard } from "./lib/mobile-local-config";

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
  const [isCopyingMobileConfig, setIsCopyingMobileConfig] = React.useState(false);
  const handleCopyMobileLocalConfig = React.useCallback(async () => {
    setIsCopyingMobileConfig(true);
    try {
      await copyMobileLocalConfigToClipboard({
        serverDomain: process.env.NEXT_PUBLIC_API_BASE_URL?.trim() || window.location.origin,
        writeText: navigator.clipboard.writeText.bind(navigator.clipboard),
      });
      toast.success("Mobile config copied");
    } catch {
      toast.error("Failed to copy mobile config");
    } finally {
      setIsCopyingMobileConfig(false);
    }
  }, []);

  return (
    <>
      <Sidebar collapsible="icon" className="relative w-54 h-full">
        <SidebarHeader className="border-b border-slate-200">
          <SidebarMenu>
            <SidebarMenuItem>
              <div className="flex items-center justify-between group-data-[collapsible=icon]:justify-center">
                <div className="min-w-0 group-data-[collapsible=icon]:hidden flex items-center justify-between w-full mr-3">
                  {/* <p className="text-xs uppercase tracking-[0.28em] text-slate-400">
                    Agw
                  </p>
                  <p className="truncate text-sm font-semibold text-black">
                    Agw-Web
                  </p> */}
                  <span>Agw</span>
                  <Button
                    variant="ghost"
                    className="cursor-pointer"
                    size="sm"
                    onClick={handleCopyMobileLocalConfig}
                    disabled={isCopyingMobileConfig}
                    title="Copy mobile local config"
                    aria-label="Copy mobile local config"
                  >
                    <Share2 size={16} />
                  </Button>
                </div>

                <SidebarTrigger className="-ml-1 group-data-[collapsible=icon]:mx-auto" />
              </div>
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
