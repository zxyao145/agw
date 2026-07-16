"use client";

import { Button } from "@/components/ui/button";

interface ModelsHeaderProps {
  onRefresh: () => void;
  isRefreshing: boolean;
  onCreateClick: () => void;
}

export function ModelsHeader({ onRefresh, isRefreshing, onCreateClick }: ModelsHeaderProps) {
  return (
    <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
      <div className="min-w-0">
        <h1 className="truncate text-xl font-semibold">Models</h1>
        <p className="text-sm text-muted-foreground">Manage LLM models.</p>
      </div>

      <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
        <Button variant="outline" onClick={onRefresh} disabled={isRefreshing}>
          Refresh
        </Button>

        <Button onClick={onCreateClick}>Create model</Button>
      </div>
    </div>
  );
}
