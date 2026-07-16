import * as React from "react";
import { UseMutationResult } from "@tanstack/react-query";
import { Button } from "@/components/ui/button";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { ButtonGroup } from "@/components/ui/button-group";
import { Pencil, Trash2, Play, Waypoints } from "lucide-react";
import type { AgentflowDto } from "@/types/agentflow";
import { getApiErrorMessage } from "@/api/utils";
import { formatLocalDateTime } from "@/lib/date-time";

interface AgentflowsTableProps {
  agentflows: AgentflowDto[];
  isLoading: boolean;
  isError: boolean;
  error: unknown;
  deleteMutation: UseMutationResult<unknown, Error, string>;
  onEdit: (agentflow: AgentflowDto) => void;
  onDelete: (agentflow: AgentflowDto) => void;
  onExecute: (agentflow: AgentflowDto) => void;
  onViewMermaid: (agentflow: AgentflowDto) => void;
}

export function AgentflowsTable({
  agentflows,
  isLoading,
  isError,
  error,
  deleteMutation,
  onEdit,
  onDelete,
  onExecute,
  onViewMermaid,
}: AgentflowsTableProps) {
  if (isLoading) {
    return <div className="text-sm text-muted-foreground">Loading...</div>;
  }

  if (isError) {
    return (
      <div className="text-sm text-destructive">
        Failed to load agentflows: {getApiErrorMessage(error)}
      </div>
    );
  }

  if (!agentflows || agentflows.length === 0) {
    return (
      <div className="text-sm text-muted-foreground">
        No agentflows found. Create one to get started.
      </div>
    );
  }

  return (
    <div className="rounded-md border">
      <Table>
        <TableHeader className="bg-muted/30">
          <TableRow>
            <TableHead>Name</TableHead>
            <TableHead>Description</TableHead>
            <TableHead>Updated</TableHead>
            <TableHead className="text-right">Actions</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {agentflows.map((agentflow) => (
            <TableRow key={agentflow.id}>
              <TableCell>
                <div className="font-medium">{agentflow.name}</div>
                <div className="font-mono text-xs break-all text-muted-foreground">
                  {agentflow.id}
                </div>
              </TableCell>
              <TableCell className="max-w-xs truncate">{agentflow.description || "-"}</TableCell>
              <TableCell className="text-xs text-muted-foreground">
                {formatLocalDateTime(agentflow.updateTime ?? agentflow.createTime)}
              </TableCell>
              <TableCell>
                <div className="flex justify-end">
                  <ButtonGroup>
                    <Button
                      variant="ghost"
                      className="cursor-pointer"
                      size="icon-sm"
                      onClick={() => onExecute(agentflow)}
                      title="Run agentflow"
                    >
                      <Play className="w-4 h-4" />
                    </Button>
                    <Button
                      variant="ghost"
                      className="cursor-pointer"
                      size="icon-sm"
                      onClick={() => onEdit(agentflow)}
                      title="Edit agentflow"
                    >
                      <Pencil className="w-4 h-4" />
                    </Button>
                    <Button
                      variant="ghost"
                      className="cursor-pointer"
                      size="icon-sm"
                      onClick={() => onViewMermaid(agentflow)}
                      title="View Mermaid chart"
                    >
                      <Waypoints className="w-4 h-4" />
                    </Button>
                    <Button
                      variant="ghost"
                      size="icon-sm"
                      onClick={() => onDelete(agentflow)}
                      disabled={deleteMutation.isPending}
                      title="Delete agentflow"
                      className="cursor-pointer text-destructive hover:text-destructive hover:bg-destructive/10"
                    >
                      <Trash2 className="w-4 h-4" />
                    </Button>
                  </ButtonGroup>
                </div>
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}
