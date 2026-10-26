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
  if (icon.kind === "svg") {
    // SVG 作为 mask 使用，形状按文字颜色填充。The SVG is a mask, so its shape takes the text color.
    return (
      <span
        aria-hidden="true"
        className={cn(
          "inline-block size-4 bg-current mask-contain mask-center mask-no-repeat",
          className,
        )}
        style={{ maskImage: `url("${icon.src}")` }}
      />
    );
  }
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
