"use client";

import * as React from "react";
import type { UseQueryResult } from "@tanstack/react-query";

import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import type { ModelProviderDto, ModelDto, ProviderDto } from "./types";
import { getApiErrorMessage } from "./utils";
import { ModelProviderActions } from "./model-provider-actions";

type ModelProviderTableProps = {
  modelProvidersQuery: UseQueryResult<ModelProviderDto[], Error>;
  modelNameById: Map<string, string>;
  providerNameById: Map<string, string>;
};

export function ModelProviderTable({
  modelProvidersQuery,
  modelNameById,
  providerNameById,
}: ModelProviderTableProps) {
  return (
    <div>
      {modelProvidersQuery.isLoading ? (
        <div className="text-sm text-muted-foreground">Loading...</div>
      ) : modelProvidersQuery.isError ? (
        <div className="text-sm text-destructive">
          Failed to load model providers: {getApiErrorMessage(modelProvidersQuery.error)}
        </div>
      ) : (modelProvidersQuery.data?.length ?? 0) === 0 ? (
        <div className="text-sm text-muted-foreground">No model providers found.</div>
      ) : (
        <div className="rounded-md border">
          <Table className="min-w-[960px]">
            <TableHeader className="bg-muted/30">
              <TableRow>
                <TableHead>Provider</TableHead>
                <TableHead>Model</TableHead>
                <TableHead className="text-right">Input</TableHead>
                <TableHead className="text-right">Output</TableHead>
                <TableHead className="text-right">Cache read</TableHead>
                <TableHead className="text-right">Cache write</TableHead>
                <TableHead className="text-right">RPS</TableHead>
                <TableHead className="text-right">Actions</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {(modelProvidersQuery.data ?? []).map((item) => {
                const modelName = modelNameById.get(item.modelId) ?? "Unknown model";
                const providerName = providerNameById.get(item.providerId) ?? "Unknown provider";

                return (
                  <TableRow key={`${item.modelId}:${item.providerId}`} className="align-top">
                    <TableCell className="min-w-48">
                      <div className="min-w-0">
                        <div className="truncate">{providerName}</div>
                        {/* <div className="truncate font-mono text-xs text-muted-foreground">
                            {item.providerId}
                          </div> */}
                      </div>
                    </TableCell>
                    <TableCell className="min-w-48">
                      <div className="min-w-0">
                        <div className="truncate">{modelName}</div>
                        {/* <div className="truncate font-mono text-xs text-muted-foreground">
                            {item.modelId}
                          </div> */}
                      </div>
                    </TableCell>
                    <TableCell className="whitespace-nowrap text-right font-mono text-xs">
                      {String(item.inputPrice)}
                    </TableCell>
                    <TableCell className="whitespace-nowrap text-right font-mono text-xs">
                      {String(item.outputPrice)}
                    </TableCell>
                    <TableCell className="whitespace-nowrap text-right font-mono text-xs">
                      {String(item.cacheRead)}
                    </TableCell>
                    <TableCell className="whitespace-nowrap text-right font-mono text-xs">
                      {String(item.cacheWrite)}
                    </TableCell>
                    <TableCell className="whitespace-nowrap text-right font-mono text-xs">
                      {String(item.rpsLimit)}
                    </TableCell>
                    <TableCell className="whitespace-nowrap text-right">
                      <ModelProviderActions
                        modelProviderId={item.id}
                        modelId={item.modelId}
                        providerId={item.providerId}
                      />
                    </TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </Table>
        </div>
      )}
    </div>
  );
}
