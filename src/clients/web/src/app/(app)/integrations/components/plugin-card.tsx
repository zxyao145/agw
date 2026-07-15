"use client";

import { Cable, Settings2 } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardFooter,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";

import type { IntegrationSelection, PluginDefinition } from "../types";

type PluginCardProps = {
  plugin: PluginDefinition;
  onConfigure: (selection: IntegrationSelection) => void;
  onCreateConnection: (selection: IntegrationSelection) => void;
};

export function PluginCard({ plugin, onConfigure, onCreateConnection }: PluginCardProps) {
  return (
    <Card className="gap-0 overflow-hidden py-0">
      <CardHeader className="border-b border-dashed bg-muted/20 p-5 [.border-b]:pb-5">
        <CardTitle className="flex flex-wrap items-center gap-2">
          {plugin.displayName}
          <Badge variant="outline">v{plugin.version}</Badge>
        </CardTitle>
        <CardDescription>{plugin.description}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4 p-5">
        {plugin.connectors.flatMap((connector) =>
          connector.authSchemes.map((authScheme) => {
            const selection = { plugin, connector, authScheme };
            return (
              <div
                key={`${connector.id}:${authScheme.id}`}
                className="flex flex-col items-start gap-3 rounded-lg border border-dashed p-4 md:grid-cols-[1fr_auto] md:items-center"
              >
                <div className="w-full">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="font-medium">{connector.displayName}</span>
                    <Badge variant="secondary">{authScheme.displayName}</Badge>
                    <Badge variant={authScheme.installation?.enabled ? "default" : "outline"}>
                      {authScheme.installation?.enabled ? "Installed" : "Needs setup"}
                    </Badge>
                  </div>
                  <p className="mt-1 text-sm text-muted-foreground">{connector.description}</p>
                </div>
                <div className="w-full flex flex-wrap gap-2 justify-end">
                  <Button variant="outline" size="sm" onClick={() => onConfigure(selection)}>
                    <Settings2 className="size-4" />
                    Configure
                  </Button>
                  <Button size="sm" onClick={() => onCreateConnection(selection)}>
                    <Cable className="size-4" />
                    New connection
                  </Button>
                </div>
              </div>
            );
          }),
        )}
      </CardContent>
      <CardFooter className="border-t border-dashed px-5 py-3 text-xs text-muted-foreground">
        {plugin.tags.join(" · ")}
      </CardFooter>
    </Card>
  );
}
