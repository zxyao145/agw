"use client";

import * as React from "react";
import { usePathname, useRouter } from "next/navigation";

import { getAuthSession } from "@/api/auth";

export function AuthGate({ children }: { children: React.ReactNode }) {
  const router = useRouter();
  const pathname = usePathname();
  const [ready, setReady] = React.useState(false);

  React.useEffect(() => {
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
  }, [pathname, router]);

  if (!ready) {
    return (
      <div className="grid min-h-screen place-items-center text-sm text-muted-foreground">
        Connecting to Agw Server…
      </div>
    );
  }

  return children;
}
