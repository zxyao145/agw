"use client";

import * as React from "react";

import { DesktopWorkspaceErrorState } from "@/runtime";

export default function Error({
  error,
  reset,
}: {
  error: Error & { digest?: string };
  reset: () => void;
}) {
  React.useEffect(() => {
    console.error(error);
  }, [error]);

  return <DesktopWorkspaceErrorState error={error} onRetry={reset} />;
}
