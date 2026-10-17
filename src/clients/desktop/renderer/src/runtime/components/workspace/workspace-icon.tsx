"use client";

import { cn } from "@agw/components";
import { icons, Puzzle } from "lucide-react";

import type { WorkspaceIcon } from "../../extensions/workspace-registry";

/** lucide-react 导出名或包内图标的 data URL。A lucide-react export name or a package icon data URL. */
export function WorkspaceIconView({
  icon,
  className,
}: {
  icon: WorkspaceIcon;
  className?: string;
}) {
  if (icon.kind === "image") {
    return (
      <img
        src={icon.src}
        alt=""
        aria-hidden="true"
        className={cn("size-4 object-contain", className)}
      />
    );
  }
  const Icon = icons[icon.name as keyof typeof icons] ?? Puzzle;
  return <Icon aria-hidden="true" className={cn("size-4", className)} />;
}
