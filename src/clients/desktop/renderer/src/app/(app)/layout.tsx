"use client";

import * as React from "react";

import { AppShell, DesktopConnectionGate, DesktopWorkspaceBoundary } from "@/runtime";

export default function AppLayout({ children }: { children: React.ReactNode }) {
  return (
    <React.Suspense fallback={<div className="min-h-screen bg-background" />}>
      <DesktopConnectionGate>
        <AppShell>
          <DesktopWorkspaceBoundary>{children}</DesktopWorkspaceBoundary>
        </AppShell>
      </DesktopConnectionGate>
    </React.Suspense>
  );
}
