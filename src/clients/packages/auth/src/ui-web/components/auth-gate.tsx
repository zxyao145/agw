"use client";

import * as React from "react";
import { usePathname, useRouter } from "next/navigation";

import { getAuthSession } from "../../services/auth";

export function AuthGate({ children }: { children: React.ReactNode }) {
  const router = useRouter();
  const pathname = usePathname();
  const currentUserId = React.useRef<string | null | undefined>(undefined);
  const [ready, setReady] = React.useState(false);

  React.useEffect(() => {
    let active = true;
    const check = () => {
      return getAuthSession()
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
          if (currentUserId.current !== undefined && currentUserId.current !== session.userId) {
            window.location.reload();
            return;
          }
          currentUserId.current = session.userId;
          setReady(true);
        })
        .catch(() => {
          if (active) router.replace("/login/?error=unavailable");
        });
    };
    void check();
    const onFocus = () => {
      void check();
    };
    window.addEventListener("focus", onFocus);
    return () => {
      active = false;
      window.removeEventListener("focus", onFocus);
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
