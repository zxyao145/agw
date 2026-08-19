"use client";

import * as React from "react";
import Link from "next/link";
import { useMutation, useQuery, useQueryClient } from "@agw/components/query";
import { toast } from "sonner";

import { apiDelete, apiGet, apiPost, apiPut } from "@agw/api";
import { getApiErrorMessage } from "@agw/api";
import {
  toEnvironmentVariableEntries,
  type ConnectionOption,
  type EnvironmentVariableEntry,
  type McpToolServerDto,
  type SkillDto,
} from "@agw/integrations";
import { Button } from "@agw/components";
import { ButtonGroup } from "@agw/components";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@agw/components";
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@agw/components";
import { Copy, Eye, MessagesSquare, Pencil, Trash2 } from "lucide-react";
import { formatLocalDateTime } from "@agw/components";

import { CreateProjectDialog } from "./components/create-project-dialog";
import { createProjectCopyRequest } from "./copy-requests";
import { EditProjectDialog } from "./components/edit-project-dialog";
import type {
  ProjectCreateRequest,
  ProjectResponse,
  ProjectUpdateMutationVariables,
} from "./components/types";
import { syncDefaultProjectWorkspace, toProjectCapabilityFormState } from "./project-form";
import { type ToolInfo, type ToolValueObject } from "@agw/tools";

function toggleSelection(setter: React.Dispatch<React.SetStateAction<string[]>>, value: string) {
  setter((current) =>
    current.includes(value)
      ? current.filter((candidate) => candidate !== value)
      : [...current, value],
  );
}

export default function ProjectsPage() {
  const queryClient = useQueryClient();

  const projectsQuery = useQuery({
    queryKey: ["projects"],
    queryFn: async () => (await apiGet("/api/projects")) as unknown as ProjectResponse[],
  });
  const toolsQuery = useQuery({
    queryKey: ["tools"],
    queryFn: async () => (await apiGet("/api/tools")) as unknown as ToolInfo[],
  });
  const mcpToolServersQuery = useQuery({
    queryKey: ["mcpToolServers"],
    queryFn: async () => (await apiGet("/api/mcp-tool-servers")) as unknown as McpToolServerDto[],
  });
  const skillsQuery = useQuery({
    queryKey: ["skills"],
    queryFn: async () => (await apiGet("/api/skills")) as unknown as SkillDto[],
  });
  const connectionsQuery = useQuery({
    queryKey: ["connections"],
    queryFn: async () =>
      (await apiGet("/api/integrations/connections")) as unknown as ConnectionOption[],
  });

  const [createOpen, setCreateOpen] = React.useState(false);
  const [name, setName] = React.useState("");
  const [description, setDescription] = React.useState("");
  const [workspace, setWorkspace] = React.useState("");
  const [extraSetting, setExtraSetting] = React.useState("{\n  \n}");
  const [selectedSkillIds, setSelectedSkillIds] = React.useState<string[]>([]);
  const [selectedConnectionIds, setSelectedConnectionIds] = React.useState<string[]>([]);
  const [tools, setTools] = React.useState<ToolValueObject[]>([]);
  const [selectedMcpToolServerIds, setSelectedMcpToolServerIds] = React.useState<string[]>([]);
  const [environmentVariables, setEnvironmentVariables] = React.useState<
    EnvironmentVariableEntry[]
  >([]);

  const handleCreateNameChange = React.useCallback(
    (nextName: string) => {
      setWorkspace((currentWorkspace) =>
        syncDefaultProjectWorkspace({
          previousName: name,
          nextName,
          currentWorkspace,
        }),
      );
      setName(nextName);
    },
    [name],
  );

  const createProjectMutation = useMutation({
    mutationFn: async (body: ProjectCreateRequest) => {
      return await apiPost("/api/projects", { body } as never);
    },
    onSuccess: async () => {
      toast.success("Project created");
      setCreateOpen(false);
      setName("");
      setDescription("");
      setWorkspace("");
      setExtraSetting("{\n  \n}");
      setTools([]);
      setSelectedSkillIds([]);
      setSelectedMcpToolServerIds([]);
      setSelectedConnectionIds([]);
      setEnvironmentVariables([]);
      await queryClient.invalidateQueries({ queryKey: ["projects"] });
    },
    onError: (error) => {
      toast.error(`Create failed: ${getApiErrorMessage(error)}`);
    },
  });

  const copyProjectMutation = useMutation({
    mutationFn: async (project: ProjectResponse) => {
      return await apiPost("/api/projects", {
        body: createProjectCopyRequest(project, crypto.randomUUID()),
      } as never);
    },
    onSuccess: async (_data, project) => {
      toast.success(`Project "${project.name}" copied`);
      await queryClient.invalidateQueries({ queryKey: ["projects"] });
    },
    onError: (error) => {
      toast.error(`Copy failed: ${getApiErrorMessage(error)}`);
    },
  });

  const handleCopyProject = (project: ProjectResponse) => {
    if (project.type !== 0 || copyProjectMutation.isPending) {
      return;
    }

    copyProjectMutation.mutate(project);
  };

  const [editOpen, setEditOpen] = React.useState(false);
  const [editingProject, setEditingProject] = React.useState<ProjectResponse | null>(null);
  const [editName, setEditName] = React.useState("");
  const [editDescription, setEditDescription] = React.useState("");
  const [editWorkspace, setEditWorkspace] = React.useState("");
  const [editExtraSetting, setEditExtraSetting] = React.useState("");
  const [editSelectedSkillIds, setEditSelectedSkillIds] = React.useState<string[]>([]);
  const [editSelectedConnectionIds, setEditSelectedConnectionIds] = React.useState<string[]>([]);
  const [editTools, setEditTools] = React.useState<ToolValueObject[]>([]);
  const [editSelectedMcpToolServerIds, setEditSelectedMcpToolServerIds] = React.useState<string[]>(
    [],
  );
  const [editEnvironmentVariables, setEditEnvironmentVariables] = React.useState<
    EnvironmentVariableEntry[]
  >([]);

  const updateProjectMutation = useMutation({
    mutationFn: async ({ project, body }: ProjectUpdateMutationVariables) => {
      if (project.type !== 0) {
        throw new Error("Built-in projects cannot be edited.");
      }

      return await apiPut("/api/projects/{id}", {
        params: { path: { id: project.id } },
        body,
      } as never);
    },
    onSuccess: async () => {
      toast.success("Project updated");
      setEditOpen(false);
      setEditingProject(null);
      await queryClient.invalidateQueries({ queryKey: ["projects"] });
    },
    onError: (error) => {
      toast.error(`Update failed: ${getApiErrorMessage(error)}`);
    },
  });

  const openEdit = React.useCallback(
    (project: ProjectResponse) => {
      if (updateProjectMutation.isPending) {
        return;
      }

      if (project.type !== 0) {
        return;
      }

      const capabilityState = toProjectCapabilityFormState(project);
      setEditingProject(project);
      setEditName(project.name ?? "");
      setEditDescription(project.description ?? "");
      setEditWorkspace(project.workspace ?? "");
      setEditExtraSetting(project.extraSetting ?? "");
      setEditTools(capabilityState.tools);
      setEditSelectedSkillIds(capabilityState.selectedSkillIds);
      setEditSelectedMcpToolServerIds(capabilityState.selectedMcpToolServerIds);
      setEditSelectedConnectionIds(capabilityState.selectedConnectionIds);
      setEditEnvironmentVariables(
        toEnvironmentVariableEntries(capabilityState.environmentVariables),
      );
      setEditOpen(true);
    },
    [updateProjectMutation.isPending],
  );

  const [deleteOpen, setDeleteOpen] = React.useState(false);
  const [deleteProject, setDeleteProject] = React.useState<ProjectResponse | null>(null);

  const openDelete = React.useCallback((project: ProjectResponse) => {
    if (project.type !== 0) {
      return;
    }
    setDeleteProject(project);
    setDeleteOpen(true);
  }, []);

  const deleteProjectMutation = useMutation({
    mutationFn: async (id: string) => {
      return await apiDelete("/api/projects/{id}", {
        params: { path: { id } },
      } as never);
    },
    onSuccess: async () => {
      toast.success("Project deleted");
      setDeleteOpen(false);
      setDeleteProject(null);
      await queryClient.invalidateQueries({ queryKey: ["projects"] });
    },
    onError: (error) => {
      toast.error(`Delete failed: ${getApiErrorMessage(error)}`);
    },
  });

  const projects = projectsQuery.data ?? [];

  return (
    <div className="space-y-6 w-full">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div className="min-w-0">
          <h1 className="truncate text-xl font-semibold">Projects</h1>
          <p className="text-sm text-muted-foreground">Manage projects and their ordered tasks.</p>
        </div>

        <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
          <Button
            variant="outline"
            onClick={() => projectsQuery.refetch()}
            disabled={projectsQuery.isFetching}
          >
            Refresh
          </Button>
          <CreateProjectDialog
            open={createOpen}
            setOpen={setCreateOpen}
            name={name}
            setName={handleCreateNameChange}
            description={description}
            setDescription={setDescription}
            workspace={workspace}
            setWorkspace={setWorkspace}
            extraSetting={extraSetting}
            setExtraSetting={setExtraSetting}
            environmentVariables={environmentVariables}
            setEnvironmentVariables={setEnvironmentVariables}
            selectedSkillIds={selectedSkillIds}
            connectionOptions={connectionsQuery.data ?? []}
            selectedConnectionIds={selectedConnectionIds}
            tools={tools}
            setTools={setTools}
            skillsQuery={skillsQuery}
            toolsQuery={toolsQuery}
            mcpToolServersQuery={mcpToolServersQuery}
            selectedMcpToolServerIds={selectedMcpToolServerIds}
            createProjectMutation={createProjectMutation}
            toggleSkill={(skillId) => toggleSelection(setSelectedSkillIds, skillId)}
            toggleConnection={(connectionId) =>
              toggleSelection(setSelectedConnectionIds, connectionId)
            }
            toggleMcpToolServer={(serverId) =>
              toggleSelection(setSelectedMcpToolServerIds, serverId)
            }
          />
        </div>
      </div>

      {projectsQuery.isLoading ? (
        <div className="text-sm text-muted-foreground">Loading...</div>
      ) : projectsQuery.isError ? (
        <div className="text-sm text-destructive">
          Failed to load projects: {getApiErrorMessage(projectsQuery.error)}
        </div>
      ) : projects.length === 0 ? (
        <div className="text-sm text-muted-foreground">No projects.</div>
      ) : (
        <div className="overflow-hidden rounded-md border">
          <Table>
            <TableHeader className="bg-muted/30">
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Description</TableHead>
                <TableHead>Type</TableHead>
                <TableHead>Updated</TableHead>
                <TableHead className="text-right">Actions</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {projects.map((project) => (
                <TableRow key={project.id}>
                  <TableCell>
                    <Link
                      href={`/projects/details/?projectId=${encodeURIComponent(project.id)}`}
                      className="font-medium underline-offset-4 hover:underline"
                    >
                      {project.name}
                    </Link>
                    <div className="font-mono text-xs break-all text-muted-foreground">
                      {project.id}
                    </div>
                  </TableCell>
                  <TableCell className="max-w-xs truncate">{project.description || "-"}</TableCell>
                  <TableCell>
                    {project.type !== 0 ? (
                      <span className="rounded-md bg-amber-500/10 px-2 py-0.5 text-xs text-amber-700 dark:text-amber-300">
                        BuiltIn
                      </span>
                    ) : (
                      <span className="rounded-md bg-blue-500/10 px-2 py-0.5 text-xs text-blue-700 dark:text-blue-300">
                        User
                      </span>
                    )}
                  </TableCell>
                  <TableCell className="text-xs text-muted-foreground">
                    {formatLocalDateTime(project.updateTime ?? project.createTime)}
                  </TableCell>
                  <TableCell className="text-right">
                    <div className="flex justify-end">
                      <ButtonGroup>
                        <Button
                          asChild
                          variant="ghost"
                          className="cursor-pointer"
                          size="icon-sm"
                          title="Open in chat"
                        >
                          <Link href={`/chat/?projectId=${encodeURIComponent(project.id)}`}>
                            <MessagesSquare className="h-4 w-4" />
                          </Link>
                        </Button>
                        <Button
                          asChild
                          variant="ghost"
                          className="cursor-pointer"
                          size="icon-sm"
                          title="View project"
                        >
                          <Link
                            href={`/projects/details/?projectId=${encodeURIComponent(project.id)}`}
                          >
                            <Eye className="w-4 h-4" />
                          </Link>
                        </Button>
                        <Button
                          variant="ghost"
                          className="cursor-pointer"
                          size="icon-sm"
                          onClick={() => openEdit(project)}
                          disabled={project.type !== 0}
                          title={
                            project.type !== 0
                              ? "Built-in projects cannot be edited"
                              : "Edit project"
                          }
                        >
                          <Pencil className="w-4 h-4" />
                        </Button>
                        <Button
                          variant="ghost"
                          size="icon-sm"
                          className="cursor-pointer"
                          onClick={() => handleCopyProject(project)}
                          disabled={project.type !== 0 || copyProjectMutation.isPending}
                          title={
                            project.type !== 0
                              ? "Built-in projects cannot be copied"
                              : "Copy project"
                          }
                          aria-label="Copy project"
                        >
                          <Copy className="w-4 h-4" />
                        </Button>
                        <Button
                          variant="ghost"
                          size="icon-sm"
                          onClick={() => openDelete(project)}
                          disabled={project.type !== 0 || deleteProjectMutation.isPending}
                          title={
                            project.type !== 0
                              ? "Built-in projects cannot be deleted"
                              : "Delete project"
                          }
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
      )}

      <EditProjectDialog
        open={editOpen}
        setOpen={setEditOpen}
        editingProject={editingProject}
        name={editName}
        setName={setEditName}
        description={editDescription}
        setDescription={setEditDescription}
        workspace={editWorkspace}
        setWorkspace={setEditWorkspace}
        extraSetting={editExtraSetting}
        setExtraSetting={setEditExtraSetting}
        environmentVariables={editEnvironmentVariables}
        setEnvironmentVariables={setEditEnvironmentVariables}
        selectedSkillIds={editSelectedSkillIds}
        connectionOptions={connectionsQuery.data ?? []}
        selectedConnectionIds={editSelectedConnectionIds}
        tools={editTools}
        setTools={setEditTools}
        skillsQuery={skillsQuery}
        toolsQuery={toolsQuery}
        mcpToolServersQuery={mcpToolServersQuery}
        selectedMcpToolServerIds={editSelectedMcpToolServerIds}
        updateProjectMutation={updateProjectMutation}
        toggleSkill={(skillId) => toggleSelection(setEditSelectedSkillIds, skillId)}
        toggleConnection={(connectionId) =>
          toggleSelection(setEditSelectedConnectionIds, connectionId)
        }
        toggleMcpToolServer={(serverId) =>
          toggleSelection(setEditSelectedMcpToolServerIds, serverId)
        }
      />

      <Dialog open={deleteOpen} onOpenChange={setDeleteOpen}>
        <DialogContent size="lg">
          <DialogHeader>
            <DialogTitle>Delete project</DialogTitle>
            <DialogDescription>
              This will permanently delete the project and its tasks (cascade delete).
            </DialogDescription>
          </DialogHeader>
          <div className="text-sm">
            <div className="font-medium">{deleteProject?.name ?? "-"}</div>
            <div className="mt-1 text-xs text-muted-foreground font-mono">
              {deleteProject?.id ?? ""}
            </div>
          </div>
          <DialogFooter>
            <DialogClose asChild>
              <Button type="button" variant="outline">
                Cancel
              </Button>
            </DialogClose>
            <Button
              type="button"
              variant="destructive"
              onClick={() => {
                if (deleteProject?.type === 0) {
                  deleteProjectMutation.mutate(deleteProject.id);
                }
              }}
              disabled={
                !deleteProject || deleteProject.type !== 0 || deleteProjectMutation.isPending
              }
            >
              {deleteProjectMutation.isPending ? "Deleting..." : "Delete"}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
