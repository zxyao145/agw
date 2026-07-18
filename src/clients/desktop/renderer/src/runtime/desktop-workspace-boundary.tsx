"use client";

import * as React from "react";
import Link from "next/link";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { LoaderCircle, MessageSquareText, RefreshCw, Settings, TriangleAlert } from "lucide-react";
import { useQueryErrorResetBoundary } from "@agw/components/query";

import { Button, Card, CardContent, CardDescription, CardHeader, CardTitle } from "@agw/components";
import { ErrorBoundary } from "@agw/components";

type DesktopWorkspaceErrorStateProps = {
  error: Error;
  onRetry(): void;
};

export function DesktopWorkspaceErrorState({ error, onRetry }: DesktopWorkspaceErrorStateProps) {
  return (
    <div
      className="grid h-full min-h-0 w-full place-items-center overflow-auto agw-scrollbar p-6"
      role="alert"
    >
      <Card className="w-full max-w-xl border-destructive/25 shadow-lg shadow-black/5">
        <CardHeader className="flex flex-row items-start gap-3 space-y-0">
          <span className="grid size-10 shrink-0 place-items-center rounded-xl bg-destructive/10 text-destructive">
            <TriangleAlert className="size-5" />
          </span>
          <div className="min-w-0 space-y-1.5">
            <CardTitle>This view couldn’t be loaded</CardTitle>
            <CardDescription>
              Desktop is still running. Use the navigation above or retry this view.
            </CardDescription>
          </div>
        </CardHeader>
        <CardContent className="space-y-4">
          <details className="rounded-xl border border-destructive/15 bg-destructive/5 px-3 py-2 text-xs">
            <summary className="cursor-pointer font-medium text-destructive">Error details</summary>
            <p className="mt-2 break-words font-mono text-muted-foreground">
              {error.message || "An unexpected rendering error occurred."}
            </p>
          </details>
          <div className="flex flex-wrap gap-2">
            <Button type="button" size="sm" onClick={onRetry}>
              <RefreshCw className="size-4" />
              Try again
            </Button>
            <Button asChild size="sm" variant="outline">
              <Link href="/desktop/chat/">
                <MessageSquareText className="size-4" />
                Back to chat
              </Link>
            </Button>
            <Button asChild size="sm" variant="ghost">
              <Link href="/settings/">
                <Settings className="size-4" />
                Open settings
              </Link>
            </Button>
          </div>
        </CardContent>
      </Card>
    </div>
  );
}

function DesktopWorkspaceErrorFallback({ error, reset }: { error: Error; reset(): void }) {
  const router = useRouter();
  const { reset: resetQueryErrors } = useQueryErrorResetBoundary();

  const handleRetry = () => {
    resetQueryErrors();
    reset();
    router.refresh();
  };

  return <DesktopWorkspaceErrorState error={error} onRetry={handleRetry} />;
}

function DesktopWorkspaceLoading() {
  return (
    <div className="grid h-full min-h-0 w-full place-items-center text-sm text-muted-foreground">
      <span className="flex items-center gap-2" role="status">
        <LoaderCircle className="size-4 animate-spin" />
        Loading view…
      </span>
    </div>
  );
}

export function DesktopWorkspaceBoundary({ children }: { children: React.ReactNode }) {
  const pathname = usePathname();
  const searchParams = useSearchParams();
  const routeKey = `${pathname}?${searchParams.toString()}`;

  return (
    <ErrorBoundary resetKeys={[routeKey]} fallback={DesktopWorkspaceErrorFallback}>
      <React.Suspense fallback={<DesktopWorkspaceLoading />}>{children}</React.Suspense>
    </ErrorBoundary>
  );
}
