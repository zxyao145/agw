"use client";

import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Trash2 } from "lucide-react";
import { toast } from "sonner";

import { apiDelete } from "@/api/client";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";

import type { ProviderDto } from "./types";
import { getApiErrorMessage } from "./utils";

interface ProvidersTableProps {
  providers?: ProviderDto[];
  isLoading: boolean;
  isError: boolean;
  error: unknown;
}

export function ProvidersTable({
  providers,
  isLoading,
  isError,
  error,
}: ProvidersTableProps) {
  const queryClient = useQueryClient();

  const deleteProviderMutation = useMutation({
    mutationFn: async (id: string) => {
      // @ts-expect-error - OpenAPI schema has incorrect top-level path parameters definition
      return await apiDelete("/api/providers/{id}", {
        params: { path: { id } },
      });
    },
    onSuccess: async () => {
      toast.success("Provider deleted");
      await queryClient.invalidateQueries({ queryKey: ["providers"] });
    },
    onError: (error) => {
      toast.error(`Delete failed: ${getApiErrorMessage(error)}`);
    },
  });

  const handleDelete = (provider: ProviderDto) => {
    const ok = window.confirm(
      `Delete provider "${provider.name}"?\n\nThis action cannot be undone.`,
    );
    if (!ok) return;
    deleteProviderMutation.mutate(provider.id);
  };

  return (
    <Card>
      <CardHeader>
        <CardTitle>Providers</CardTitle>
        <CardDescription>
          Fetched from <code>/api/providers</code>.
        </CardDescription>
      </CardHeader>
      <CardContent>
        {isLoading ? (
          <div className="text-sm text-muted-foreground">Loading...</div>
        ) : isError ? (
          <div className="text-sm text-destructive">
            Failed to load providers: {getApiErrorMessage(error)}
          </div>
        ) : providers && providers.length > 0 ? (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Provider Type</TableHead>
                <TableHead>Description</TableHead>
                <TableHead>Endpoint</TableHead>
                <TableHead>Created</TableHead>
                <TableHead className="w-24 text-right">Actions</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {providers.map((provider) => (
                <TableRow key={provider.id}>
                  <TableCell className="font-medium">{provider.name}</TableCell>
                  <TableCell>{provider.providerType}</TableCell>
                  <TableCell className="max-w-xs truncate">
                    {provider.description || "-"}
                  </TableCell>
                  <TableCell className="max-w-sm truncate font-mono text-xs">
                    {provider.endpoint}
                  </TableCell>
                  <TableCell className="text-xs text-muted-foreground">
                    {provider.createTime
                      ? new Date(provider.createTime).toLocaleString()
                      : "-"}
                  </TableCell>
                  <TableCell className="text-right">
                    <Button
                      type="button"
                      variant="destructive"
                      size="sm"
                      disabled={deleteProviderMutation.isPending}
                      onClick={() => handleDelete(provider)}
                    >
                      <Trash2 className="h-4 w-4" />
                    </Button>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        ) : (
          <div className="text-sm text-muted-foreground">
            No providers found. Create one to get started.
          </div>
        )}
      </CardContent>
    </Card>
  );
}
