"use client";

import * as React from "react";

import {
  AppShell,
  DesktopConnectionGate,
  DesktopWorkspaceBoundary,
  ExtensionHostProvider,
} from "@/runtime";

export default function AppLayout({ children }: { children: React.ReactNode }) {
  return (
    <React.Suspense fallback={<div className="min-h-screen bg-background" />}>
      {/* extension 宿主位于连接门外层：切换 Server 时外壳卸载不会关闭 extension。 */}
      {/* The extension host sits outside the connection gate, so shell unmounts never close extensions. */}
      <ExtensionHostProvider>
        <DesktopConnectionGate>
          <AppShell>
            <DesktopWorkspaceBoundary>{children}</DesktopWorkspaceBoundary>
          </AppShell>
        </DesktopConnectionGate>
      </ExtensionHostProvider>
    </React.Suspense>
  );
}
