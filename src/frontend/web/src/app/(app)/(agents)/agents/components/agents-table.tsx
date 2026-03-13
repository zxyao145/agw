import * as React from "react";
import { UseQueryResult } from "@tanstack/react-query";
import { Button } from "@/components/ui/button";
import { ButtonGroup } from "@/components/ui/button-group";
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
import { Pencil, Trash2, Play } from "lucide-react";
import type { AgentDto } from "./types";
import { getApiErrorMessage } from "./utils";

interface AgentsTableProps {
  agentsQuery: UseQueryResult<AgentDto[], Error>;
  onEdit: (agent: AgentDto) => void;
  onDelete: (agent: AgentDto) => void;
  onExecute: (agent: AgentDto) => void;
}

export function AgentsTable({
  agentsQuery,
  onEdit,
  onDelete,
  onExecute,
}: AgentsTableProps) {
  return (
    <Card>
      <CardContent>
        {agentsQuery.isLoading ? (
          <div className="text-sm text-muted-foreground">Loading...</div>
        ) : agentsQuery.isError ? (
          <div className="text-sm text-destructive">
            Failed to load agents: {getApiErrorMessage(agentsQuery.error)}
          </div>
        ) : agentsQuery.data && agentsQuery.data.length > 0 ? (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead className="min-w-40">Display Name</TableHead>
                <TableHead>Type</TableHead>
                <TableHead>Description</TableHead>
                <TableHead>System Prompt</TableHead>
                <TableHead>Tools</TableHead>
                <TableHead>Created</TableHead>
                <TableHead className="text-right">Actions</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {agentsQuery.data.map((agent) => {
                let toolNames: string[] = [];
                try {
                  toolNames = agent.tools ? JSON.parse(agent.tools) : [];
                } catch {
                  toolNames = [];
                }

                return (
                  <TableRow key={agent.id}>
                    <TableCell className="font-medium">
                      {agent.name}
                    </TableCell>
                     <TableCell className="font-medium">
                      {agent.displayName}
                    </TableCell>
                    <TableCell>
                      <span
                        className={`text-xs px-2 py-1 rounded-full ${
                          agent.type === 0
                            ? "bg-blue-100 text-blue-700 dark:bg-blue-900 dark:text-blue-300"
                            : "bg-green-100 text-green-700 dark:bg-green-900 dark:text-green-300"
                        }`}
                      >
                        {agent.type === 0 ? "System" : "External"}
                      </span>
                    </TableCell>
                    <TableCell className="max-w-xs truncate">
                      {agent.description || "-"}
                    </TableCell>
                    <TableCell className="max-w-xs truncate">
                      {agent.systemPrompt || "-"}
                    </TableCell>
                    <TableCell className="max-w-xs">
                      {toolNames.length > 0 ? (
                        <span className="text-xs">
                          {toolNames.slice(0, 2).join(", ")}
                          {toolNames.length > 2 &&
                            ` +${toolNames.length - 2} more`}
                        </span>
                      ) : (
                        <span className="text-muted-foreground">-</span>
                      )}
                    </TableCell>
                    <TableCell className="text-xs text-muted-foreground">
                      {agent.createTime
                        ? new Date(agent.createTime).toLocaleString()
                        : "-"}
                    </TableCell>
                    <TableCell className="text-right">
                      <div className="flex justify-end gap-2">
                        <ButtonGroup>
                          <Button
                            variant="ghost"
                            className="cursor-pointer"
                            size="sm"
                            onClick={() => onExecute(agent)}
                          >
                            <Play className="w-4 h-4" />
                          </Button>
                          <Button
                            variant="ghost"
                            className="cursor-pointer"
                            size="sm"
                            onClick={() => onEdit(agent)}
                          >
                            <Pencil className="w-4 h-4" />
                          </Button>
                          <Button
                            variant="ghost"
                            size="sm"
                            onClick={() => onDelete(agent)}
                            className="cursor-pointer text-destructive hover:text-destructive hover:bg-destructive/10"
                          >
                            <Trash2 className="w-4 h-4" />
                          </Button>
                        </ButtonGroup>
                      </div>
                    </TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </Table>
        ) : (
          <div className="text-sm text-muted-foreground">
            No agents found. Create one to get started.
          </div>
        )}
      </CardContent>
    </Card>
  );
}
