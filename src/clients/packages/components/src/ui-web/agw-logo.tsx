import * as React from "react";

import { cn } from "../lib/cn";

export type AgwLogoProps = {
  className?: string;
  label?: string;
  labelClassName?: string;
  markClassName?: string;
  showLabel?: boolean;
};

export function AgwLogo({
  className,
  label = "Agw",
  labelClassName,
  markClassName,
  showLabel = true,
}: AgwLogoProps) {
  return (
    <span
      className={cn("inline-flex shrink-0 items-center gap-2 whitespace-nowrap", className)}
      aria-label={showLabel ? undefined : label}
      role={showLabel ? undefined : "img"}
    >
      <img
        src="/icon.svg"
        alt=""
        aria-hidden="true"
        className={cn("size-8 shrink-0", markClassName)}
      />
      {showLabel ? <span className={labelClassName}>{label}</span> : null}
    </span>
  );
}
