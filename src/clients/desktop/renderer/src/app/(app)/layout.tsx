"use client";

import * as React from "react";

import { QueryErrorBoundary } from "@agw/components";
import { AppShell, DesktopConnectionGate } from "@/runtime";

export default function AppLayout({ children }: { children: React.ReactNode }) {
  return (
    <React.Suspense fallback={<div className="min-h-screen bg-background" />}>
      <DesktopConnectionGate>
        <AppShell>
          <QueryErrorBoundary>{children}</QueryErrorBoundary>
        </AppShell>
      </DesktopConnectionGate>
    </React.Suspense>
  );
}
