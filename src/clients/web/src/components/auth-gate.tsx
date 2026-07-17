"use client";

import * as React from "react";
import { usePathname, useRouter } from "next/navigation";

import { getAuthSession } from "@/api/auth";
import { Button } from "@/components/ui/button";
import { useDesktopRuntime } from "@/features/desktop";
import Link from "next/link";

export function AuthGate({ children }: { children: React.ReactNode }) {
  const router = useRouter();
  const pathname = usePathname();
  const desktop = useDesktopRuntime();
  const [ready, setReady] = React.useState(false);

  React.useEffect(() => {
    if (desktop.isDesktop) {
      setReady(desktop.status === "ready" || pathname.startsWith("/settings"));
      return;
    }
    let active = true;
    getAuthSession()
      .then((session) => {
        if (!active) return;
        if (session.apiMajorVersion !== 1) {
          router.replace("/login/?error=incompatible-server");
          return;
        }
        if (!session.authenticated) {
          const query = window.location.search.replace(/^\?/, "");
          const returnUrl = `${pathname}${query ? `?${query}` : ""}`;
          router.replace(`/login/?returnUrl=${encodeURIComponent(returnUrl)}`);
          return;
        }
        setReady(true);
      })
      .catch(() => router.replace("/login/?error=unavailable"));
    return () => {
      active = false;
    };
  }, [desktop.isDesktop, desktop.status, pathname, router]);

  if (desktop.isDesktop && desktop.status !== "ready" && !pathname.startsWith("/settings")) {
    const message =
      desktop.status === "loading"
        ? "Connecting to Agw Server…"
        : desktop.status === "setup-required"
          ? "Complete Server setup in the Desktop window."
          : desktop.status === "authentication-required"
            ? "Add an API token for this Server in Settings."
            : desktop.status === "incompatible"
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

  if (!ready) {
    return (
      <div className="grid min-h-screen place-items-center text-sm text-muted-foreground">
        Connecting to Agw Server…
      </div>
    );
  }

  return children;
}
