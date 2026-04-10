"use client";

import { Fingerprint, Plus, Sparkles } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { cn } from "@/lib/utils";

import { formatIntegrationCategory, type AppDefinitionItem } from "../types";

type AppDefinitionCardProps = {
  definition: AppDefinitionItem;
  onSelect: (definition: AppDefinitionItem) => void;
};

export function AppDefinitionCard({ definition, onSelect }: AppDefinitionCardProps) {
  return (
    <Card
      className={cn(
        "h-full gap-0 overflow-hidden border-border/70 bg-gradient-to-br from-background via-background to-muted/40 py-0 transition duration-200",
        "group-hover:-translate-y-0.5 group-hover:border-primary/35 group-hover:shadow-md",
        "group-focus-visible:border-primary/50 group-focus-visible:ring-ring/50 group-focus-visible:ring-2 group-focus-visible:ring-offset-2",
        "max-w-100",
      )}
      aria-label={`Create ${definition.displayName} connection`}
    >
      <CardHeader className="border-b [.border-b]:pb-3 p-3 border-dashed">
        <div className="flex items-start justify-between">
          <div className="space-y-2">
            <CardTitle className="text-base">{definition.displayName}</CardTitle>
            <div className="flex flex-wrap items-center gap-2">
              <Badge variant="secondary">{formatIntegrationCategory(definition.category)}</Badge>
              <Badge variant="outline">{definition.provider}</Badge>
            </div>
          </div>
          <div className="">
            <button
              type="button"
              onClick={() => onSelect(definition)}
              className="cursor-pointer rounded-full border border-primary/20 bg-primary/5 p-2 text-primary transition group-hover:border-primary/40 group-hover:bg-primary/10"
              aria-label={`Create ${definition.displayName} connection`}
            >
              <Plus className="size-4" />
            </button>
          </div>
        </div>
      </CardHeader>

      <CardContent className="space-y-4 p-3">
        <p className="min-h-12 text-sm leading-6 text-muted-foreground">{definition.description}</p>

        <div className="space-y-2">
          <div className="flex items-center gap-2 text-xs font-medium uppercase tracking-[0.16em] text-muted-foreground">
            <Fingerprint className="size-3.5" />
            Scopes
          </div>
          <div className="flex flex-wrap gap-1.5">
            {definition.scopes.map((scope) => (
              <Badge key={scope} variant="outline" className="max-w-full truncate">
                {scope}
              </Badge>
            ))}
          </div>
        </div>

        {definition.tags.length > 0 || definition.toolNames.length > 0 ? (
          <div className="grid gap-2 sm:grid-cols-1">
            <div className="rounded-lg border border-dashed bg-muted/20 p-3">
              <div className="mb-2 text-xs font-medium uppercase tracking-[0.16em] text-muted-foreground">
                Tags
              </div>
              <div className="flex flex-wrap gap-1.5">
                {definition.tags.length > 0 ? (
                  definition.tags.map((tag) => (
                    <Badge key={tag} variant="secondary">
                      {tag}
                    </Badge>
                  ))
                ) : (
                  <span className="text-sm text-muted-foreground">No tags</span>
                )}
              </div>
            </div>
            <div className="rounded-lg border border-dashed bg-muted/20 p-3">
              <div className="mb-2 flex items-center gap-2 text-xs font-medium uppercase tracking-[0.16em] text-muted-foreground">
                <Sparkles className="size-3.5" />
                Tools
              </div>
              <div className="text-sm text-muted-foreground">
                {definition.toolNames.length > 0
                  ? definition.toolNames.join(", ")
                  : "No related tools"}
              </div>
            </div>
          </div>
        ) : null}
      </CardContent>
    </Card>
  );
}
