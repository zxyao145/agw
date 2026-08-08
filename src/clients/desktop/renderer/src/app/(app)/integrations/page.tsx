"use client";

import { IntegrationsPage } from "@agw/integrations";

export default function DesktopIntegrationsPage() {
  return (
    <IntegrationsPage
      completionTarget="Desktop"
      openAuthorization={async (url) => {
        const bridge = window.agwDesktop;
        if (!bridge) throw new Error("Agw Desktop bridge is unavailable.");
        await bridge.openExternal(url);
      }}
    />
  );
}
