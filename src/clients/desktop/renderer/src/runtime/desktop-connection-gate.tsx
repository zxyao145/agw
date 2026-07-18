"use client";

import * as React from "react";
import Link from "next/link";
import { usePathname } from "next/navigation";

import { Button } from "@agw/components";
import { useDesktopRuntime } from "./runtime-provider";

export function DesktopConnectionGate({ children }: { children: React.ReactNode }) {
  const pathname = usePathname();
  const desktop = useDesktopRuntime();

  if (
    desktop.status === "ready" ||
    desktop.status === "authentication-required" ||
    pathname.startsWith("/settings")
  ) {
    return children;
  }

  if (desktop.status === "loading" || desktop.status === "setup-required") {
    return <div className="min-h-screen bg-background" />;
  }

  const message =
    desktop.status === "incompatible"
      ? "This Desktop requires Server API major version 1."
      : desktop.error || "Agw Server is unavailable.";

  return (
    <div className="grid min-h-screen place-items-center bg-background p-6 text-foreground">
      <div className="w-full max-w-md rounded-2xl border bg-card p-6 shadow-sm">
        <p className="text-xs font-semibold uppercase tracking-[0.16em] text-primary">
          Server connection
        </p>
        <h1 className="mt-2 text-xl font-semibold">Agw Desktop needs attention</h1>
        <p className="mt-2 text-sm text-muted-foreground">{message}</p>
        <div className="mt-5 flex gap-2">
          <Button onClick={() => void desktop.refresh()}>Try again</Button>
          <Button asChild variant="outline">
            <Link href="/settings/">Open settings</Link>
          </Button>
        </div>
      </div>
    </div>
  );
}
