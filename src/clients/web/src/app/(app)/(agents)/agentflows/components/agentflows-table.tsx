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
import type { AgentflowDto, AgentflowSaveRequest } from "@/types/agentflow";
import { getApiErrorMessage } from "@/api/utils";
import { formatLocalDateTime } from "@/lib/date-time";

interface AgentflowsTableProps {
  agentflows: AgentflowDto[];
  isLoading: boolean;
  isError: boolean;
  error: unknown;
  updateMutation: UseMutationResult<unknown, Error, { id: string; body: AgentflowSaveRequest }>;
  deleteMutation: UseMutationResult<unknown, Error, string>;
  onToggleEnabled: (agentflow: AgentflowDto) => void;
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
  updateMutation,
  deleteMutation,
  onToggleEnabled,
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
            <TableHead>Mode</TableHead>
            <TableHead>Created</TableHead>
            <TableHead className="text-center">Enabled</TableHead>
            <TableHead className="text-right">Actions</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {agentflows.map((agentflow) => (
            <TableRow key={agentflow.id}>
              <TableCell className="font-medium">{agentflow.name}</TableCell>
              <TableCell className="max-w-xs truncate">{agentflow.description || "-"}</TableCell>
              <TableCell>DAG</TableCell>
              <TableCell className="text-xs text-muted-foreground">
                {formatLocalDateTime(agentflow.createTime)}
              </TableCell>
              <TableCell className="text-center">
                <label className="relative inline-flex items-center cursor-pointer">
                  <input
                    type="checkbox"
                    checked={agentflow.enable}
                    onChange={() => onToggleEnabled(agentflow)}
                    disabled={updateMutation.isPending}
                    className="sr-only peer"
                  />
                  <div className="w-11 h-6 bg-gray-200 peer-focus:outline-none peer-focus:ring-4 peer-focus:ring-blue-300 rounded-full peer peer-checked:after:translate-x-full rtl:peer-checked:after:-translate-x-full peer-checked:after:border-white after:content-[''] after:absolute after:top-[2px] after:start-[2px] after:bg-white after:border-gray-300 after:border after:rounded-full after:h-5 after:w-5 after:transition-all peer-checked:bg-green-600"></div>
                </label>
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
