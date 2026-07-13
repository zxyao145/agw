"use client";

import * as React from "react";
import Link from "next/link";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";

import { apiDelete, apiGet, apiPost, apiPut } from "@/api/client";
import { getApiErrorMessage } from "@/api/utils";
import {
  filterAppOptions,
  toEnvironmentVariableEntries,
  type AppInstanceOption,
  type EnvironmentVariableEntry,
  type McpToolServerDto,
  type SkillDto,
  type ToolInfo,
} from "@/components/definition-capabilities";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { formatLocalDateTime } from "@/lib/date-time";

import { CreateProjectDialog } from "./components/create-project-dialog";
import { EditProjectDialog } from "./components/edit-project-dialog";
import type {
  ProjectCreateRequest,
  ProjectResponse,
  ProjectUpdateMutationVariables,
} from "./components/types";
import { syncDefaultProjectWorkspace, toProjectCapabilityFormState } from "./project-form";

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
  const appInstancesQuery = useQuery({
    queryKey: ["appInstances"],
    queryFn: async () =>
      (await apiGet("/api/integrations/app-instances")) as unknown as AppInstanceOption[],
  });

  const [createOpen, setCreateOpen] = React.useState(false);
  const [name, setName] = React.useState("");
  const [description, setDescription] = React.useState("");
  const [workspace, setWorkspace] = React.useState("");
  const [enable, setEnable] = React.useState(true);
  const [extraSetting, setExtraSetting] = React.useState("{\n  \n}");
  const [selectedSkillIds, setSelectedSkillIds] = React.useState<string[]>([]);
  const [selectedAppInstanceIds, setSelectedAppInstanceIds] = React.useState<string[]>([]);
  const [appSearchTerm, setAppSearchTerm] = React.useState("");
  const [selectedTools, setSelectedTools] = React.useState<string[]>([]);
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
      setEnable(true);
      setExtraSetting("{\n  \n}");
      setSelectedTools([]);
      setSelectedSkillIds([]);
      setSelectedMcpToolServerIds([]);
      setSelectedAppInstanceIds([]);
      setAppSearchTerm("");
      setEnvironmentVariables([]);
      await queryClient.invalidateQueries({ queryKey: ["projects"] });
    },
    onError: (error) => {
      toast.error(`Create failed: ${getApiErrorMessage(error)}`);
    },
  });

  const [editOpen, setEditOpen] = React.useState(false);
  const [editingProject, setEditingProject] = React.useState<ProjectResponse | null>(null);
  const [editName, setEditName] = React.useState("");
  const [editDescription, setEditDescription] = React.useState("");
  const [editWorkspace, setEditWorkspace] = React.useState("");
  const [editEnable, setEditEnable] = React.useState(true);
  const [editExtraSetting, setEditExtraSetting] = React.useState("");
  const [editSelectedSkillIds, setEditSelectedSkillIds] = React.useState<string[]>([]);
  const [editSelectedAppInstanceIds, setEditSelectedAppInstanceIds] = React.useState<string[]>([]);
  const [editAppSearchTerm, setEditAppSearchTerm] = React.useState("");
  const [editSelectedTools, setEditSelectedTools] = React.useState<string[]>([]);
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
      setEditEnable(Boolean(project.enable));
      setEditExtraSetting(project.extraSetting ?? "");
      setEditSelectedTools(capabilityState.selectedTools);
      setEditSelectedSkillIds(capabilityState.selectedSkillIds);
      setEditSelectedMcpToolServerIds(capabilityState.selectedMcpToolServerIds);
      setEditSelectedAppInstanceIds(capabilityState.selectedAppInstanceIds);
      setEditEnvironmentVariables(
        toEnvironmentVariableEntries(capabilityState.environmentVariables),
      );
      setEditAppSearchTerm("");
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

  const filteredAppOptions = React.useMemo(
    () => filterAppOptions(appInstancesQuery.data ?? [], appSearchTerm),
    [appInstancesQuery.data, appSearchTerm],
  );
  const filteredEditAppOptions = React.useMemo(
    () => filterAppOptions(appInstancesQuery.data ?? [], editAppSearchTerm),
    [appInstancesQuery.data, editAppSearchTerm],
  );
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
            enable={enable}
            setEnable={setEnable}
            extraSetting={extraSetting}
            setExtraSetting={setExtraSetting}
            environmentVariables={environmentVariables}
            setEnvironmentVariables={setEnvironmentVariables}
            selectedSkillIds={selectedSkillIds}
            appOptions={appInstancesQuery.data ?? []}
            selectedAppInstanceIds={selectedAppInstanceIds}
            appSearchTerm={appSearchTerm}
            setAppSearchTerm={setAppSearchTerm}
            filteredAppOptions={filteredAppOptions}
            selectedTools={selectedTools}
            skillsQuery={skillsQuery}
            toolsQuery={toolsQuery}
            mcpToolServersQuery={mcpToolServersQuery}
            selectedMcpToolServerIds={selectedMcpToolServerIds}
            createProjectMutation={createProjectMutation}
            toggleSkill={(skillId) => toggleSelection(setSelectedSkillIds, skillId)}
            toggleAppInstance={(appId) => toggleSelection(setSelectedAppInstanceIds, appId)}
            toggleTool={(toolName) => toggleSelection(setSelectedTools, toolName)}
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
        <div className="space-y-3">
          {projects.map((project) => (
            <div
              key={project.id}
              className="flex flex-col gap-3 rounded-lg border p-4 sm:flex-row sm:items-start sm:justify-between"
            >
              <div className="min-w-0 space-y-1">
                <div className="flex min-w-0 flex-wrap items-center gap-2">
                  <Link
                    href={`/projects/details/?projectId=${encodeURIComponent(project.id)}`}
                    className="truncate font-medium underline-offset-4 hover:underline"
                  >
                    {project.name}
                  </Link>
                  {project.type !== 0 ? (
                    <span className="rounded-md bg-amber-500/10 px-2 py-0.5 text-xs text-amber-700 dark:text-amber-300">
                      BuiltIn
                    </span>
                  ) : null}
                  <span
                    className={
                      project.enable
                        ? "rounded-md bg-emerald-500/10 px-2 py-0.5 text-xs text-emerald-700 dark:text-emerald-300"
                        : "rounded-md bg-muted px-2 py-0.5 text-xs text-muted-foreground"
                    }
                  >
                    {project.enable ? "Enabled" : "Disabled"}
                  </span>
                </div>
                {project.description ? (
                  <div className="text-sm text-muted-foreground">{project.description}</div>
                ) : null}
                <div className="text-xs text-muted-foreground">
                  <span className="font-mono">{project.id}</span>
                  <span className="mx-2">·</span>
                  Updated: {formatLocalDateTime(project.updateTime ?? project.createTime)}
                </div>
              </div>

              <div className="flex flex-wrap gap-2 sm:justify-end">
                <Button asChild variant="outline" size="sm">
                  <Link href={`/projects/details/?projectId=${encodeURIComponent(project.id)}`}>
                    View
                  </Link>
                </Button>
                <Button
                  variant="outline"
                  size="sm"
                  onClick={() => openEdit(project)}
                  disabled={project.type !== 0}
                  title={project.type !== 0 ? "Built-in projects cannot be edited" : undefined}
                >
                  Edit
                </Button>
                <Button
                  variant="destructive"
                  size="sm"
                  onClick={() => openDelete(project)}
                  disabled={project.type !== 0 || deleteProjectMutation.isPending}
                  title={project.type !== 0 ? "Built-in projects cannot be deleted" : undefined}
                >
                  Delete
                </Button>
              </div>
            </div>
          ))}
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
        enable={editEnable}
        setEnable={setEditEnable}
        extraSetting={editExtraSetting}
        setExtraSetting={setEditExtraSetting}
        environmentVariables={editEnvironmentVariables}
        setEnvironmentVariables={setEditEnvironmentVariables}
        selectedSkillIds={editSelectedSkillIds}
        appOptions={appInstancesQuery.data ?? []}
        selectedAppInstanceIds={editSelectedAppInstanceIds}
        appSearchTerm={editAppSearchTerm}
        setAppSearchTerm={setEditAppSearchTerm}
        filteredAppOptions={filteredEditAppOptions}
        selectedTools={editSelectedTools}
        skillsQuery={skillsQuery}
        toolsQuery={toolsQuery}
        mcpToolServersQuery={mcpToolServersQuery}
        selectedMcpToolServerIds={editSelectedMcpToolServerIds}
        updateProjectMutation={updateProjectMutation}
        toggleSkill={(skillId) => toggleSelection(setEditSelectedSkillIds, skillId)}
        toggleAppInstance={(appId) => toggleSelection(setEditSelectedAppInstanceIds, appId)}
        toggleTool={(toolName) => toggleSelection(setEditSelectedTools, toolName)}
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
