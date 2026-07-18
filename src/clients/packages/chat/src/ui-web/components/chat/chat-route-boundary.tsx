"use client";

import * as React from "react";
import { usePathname, useRouter, useSearchParams } from "next/navigation";

import { getChatRouteRedirect } from "../../../lib/chat-route";
import { useExecutionPlatform } from "../../execution-platform";

export function ChatRouteBoundary({ children }: { children: React.ReactNode }) {
  const platform = useExecutionPlatform();
  const pathname = usePathname();
  const router = useRouter();
  const searchParams = useSearchParams();
  const search = searchParams.toString();
  const redirectHref = getChatRouteRedirect({
    isDesktop: platform.isDesktop,
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
