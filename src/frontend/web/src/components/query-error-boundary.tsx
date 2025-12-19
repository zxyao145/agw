"use client";

import * as React from "react";
import { useQueryErrorResetBoundary } from "@tanstack/react-query";
import { ErrorBoundary } from "./error-boundary";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";

interface QueryErrorBoundaryProps {
  children: React.ReactNode;
}

function getApiErrorMessage(error: unknown): string {
  if (error instanceof Error) return error.message;
  if (typeof error === "string") return error;
  return "An unexpected error occurred";
}

function getApiErrorStatus(error: unknown): number | null {
  if (error && typeof error === "object" && "status" in error) {
    return typeof error.status === "number" ? error.status : null;
  }
  return null;
}

function ApiErrorFallback({
  error,
  reset,
}: {
  error: Error;
  reset: () => void;
}) {
  const status = getApiErrorStatus(error);
  const message = getApiErrorMessage(error);

  let title = "API Error";
  let description = "Failed to load data from the server.";

  if (status === 404) {
    title = "Not Found";
    description = "The requested resource could not be found.";
  } else if (status === 403) {
    title = "Access Denied";
    description = "You don't have permission to access this resource.";
  } else if (status === 401) {
    title = "Unauthorized";
    description = "Please log in to access this resource.";
  } else if (status && status >= 500) {
    title = "Server Error";
    description = "The server encountered an error. Please try again later.";
  } else if (status && status >= 400) {
    title = "Request Error";
    description = "There was a problem with your request.";
  }

  return (
    <div className="flex items-center justify-center min-h-[300px] p-4">
      <Card className="max-w-lg border-destructive/50">
        <CardHeader>
          <div className="flex items-center gap-2">
            <CardTitle className="text-destructive">{title}</CardTitle>
            {status && (
              <span className="text-sm font-mono text-muted-foreground">
                ({status})
              </span>
            )}
          </div>
          <CardDescription>{description}</CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2">
            <p className="text-sm text-destructive break-words">{message}</p>
          </div>
          <div className="flex gap-2">
            <Button onClick={reset} variant="outline" size="sm">
              Try again
            </Button>
            <Button
              onClick={() => window.history.back()}
              variant="ghost"
              size="sm"
            >
              Go back
            </Button>
          </div>
        </CardContent>
      </Card>
    </div>
  );
}

export function QueryErrorBoundary({ children }: QueryErrorBoundaryProps) {
  const { reset } = useQueryErrorResetBoundary();

  return (
    <ErrorBoundary
      fallback={(props) => <ApiErrorFallback {...props} reset={() => {
        props.reset();
        reset();
      }} />}
    >
      {children}
    </ErrorBoundary>
  );
}
