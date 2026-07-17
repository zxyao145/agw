"use client";

import * as React from "react";
import { usePathname, useRouter, useSearchParams } from "next/navigation";

import { useDesktopRuntime } from "@/features/desktop";
import { getChatRouteRedirect } from "@/lib/chat-route";

export function ChatRouteBoundary({ children }: { children: React.ReactNode }) {
  const desktop = useDesktopRuntime();
  const pathname = usePathname();
  const router = useRouter();
  const searchParams = useSearchParams();
  const search = searchParams.toString();
  const redirectHref = getChatRouteRedirect({
    isDesktop: desktop.isDesktop,
    pathname,
    search,
  });

  React.useEffect(() => {
    if (redirectHref) {
      router.replace(redirectHref, { scroll: false });
    }
  }, [redirectHref, router]);

  if (redirectHref) {
    return (
      <div className="grid h-full min-h-64 w-full place-items-center text-sm text-muted-foreground">
        <span role="status">Loading chat…</span>
      </div>
    );
  }

  return children;
}
