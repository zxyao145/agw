"use client"

import * as React from "react"
import Link from "next/link"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { toast } from "sonner"

import { ApiError, apiDelete, apiGet, apiPost, apiPut } from "@/api/client"
import type { components } from "@/api/openapi"
import { Button } from "@/components/ui/button"
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card"
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription as UiDialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle as UiDialogTitle,
  DialogTrigger,
} from "@/components/ui/dialog"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Textarea } from "@/components/ui/textarea"

type ProjectCreateRequest = components["schemas"]["ProjectCreateRequest"]
type ProjectUpdateRequest = components["schemas"]["ProjectUpdateRequest"]

type ProjectDto = {
  id: string
  name: string
  description: string | null
  enable: boolean
  createBy?: string | null
  createTime?: string | null
  updateBy?: string | null
  updateTime?: string | null
}

function formatDate(value?: string | null): string {
  if (!value) return "-"
  const d = new Date(value)
  if (Number.isNaN(d.getTime())) return value
  return d.toLocaleString()
}

function getApiErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (typeof error.body === "string" && error.body.trim().length) {
      return error.body
    }
    if (error.body && typeof error.body === "object") {
      try {
        return JSON.stringify(error.body)
      } catch {
        // ignore
      }
    }
    return `${error.status} ${error.statusText}`
  }
  if (error instanceof Error) return error.message
  return "Unknown error"
}

export default function ProjectsPage() {
  const queryClient = useQueryClient()

  const projectsQuery = useQuery({
    queryKey: ["projects"],
    queryFn: async () => {
      // Backend returns entity JSON but OpenAPI doesn't declare response schemas yet.
      return (await apiGet("/api/projects")) as unknown as ProjectDto[]
    },
  })

  const [createOpen, setCreateOpen] = React.useState(false)
  const [name, setName] = React.useState("")
  const [description, setDescription] = React.useState<string>("")
  const [enable, setEnable] = React.useState(true)

  const createProjectMutation = useMutation({
    mutationFn: async (body: ProjectCreateRequest) => {
      // Backend returns 201 Created with body, OpenAPI marks only 200. We treat it as unknown.
      return await apiPost("/api/projects", { body })
    },
    onSuccess: async () => {
      toast.success("Project created")
      setCreateOpen(false)
      setName("")
      setDescription("")
      setEnable(true)
      await queryClient.invalidateQueries({ queryKey: ["projects"] })
    },
    onError: (error) => {
      toast.error(`Create failed: ${getApiErrorMessage(error)}`)
    },
  })

  const [editOpen, setEditOpen] = React.useState(false)
  const [editProjectId, setEditProjectId] = React.useState<string | null>(null)
  const [editName, setEditName] = React.useState("")
  const [editDescription, setEditDescription] = React.useState<string>("")
  const [editEnable, setEditEnable] = React.useState(true)

  const openEdit = React.useCallback((project: ProjectDto) => {
    setEditProjectId(project.id)
    setEditName(project.name ?? "")
    setEditDescription(project.description ?? "")
    setEditEnable(Boolean(project.enable))
    setEditOpen(true)
  }, [])

  const updateProjectMutation = useMutation({
    mutationFn: async (args: { id: string; body: ProjectUpdateRequest }) => {
      return await apiPut("/api/projects/{id}", {
        params: { path: { id: args.id } },
        body: args.body,
      })
    },
    onSuccess: async () => {
      toast.success("Project updated")
      setEditOpen(false)
      setEditProjectId(null)
      await queryClient.invalidateQueries({ queryKey: ["projects"] })
    },
    onError: (error) => {
      toast.error(`Update failed: ${getApiErrorMessage(error)}`)
    },
  })

  const [deleteOpen, setDeleteOpen] = React.useState(false)
  const [deleteProject, setDeleteProject] = React.useState<ProjectDto | null>(null)

  const openDelete = React.useCallback((project: ProjectDto) => {
    setDeleteProject(project)
    setDeleteOpen(true)
  }, [])

  const deleteProjectMutation = useMutation({
    mutationFn: async (id: string) => {
      return await apiDelete("/api/projects/{id}", { params: { path: { id } } } as never)
    },
    onSuccess: async () => {
      toast.success("Project deleted")
      setDeleteOpen(false)
      setDeleteProject(null)
      await queryClient.invalidateQueries({ queryKey: ["projects"] })
    },
    onError: (error) => {
      toast.error(`Delete failed: ${getApiErrorMessage(error)}`)
    },
  })

  const projects = projectsQuery.data ?? []

  return (
    <div className="space-y-6">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div className="min-w-0">
          <h1 className="truncate text-xl font-semibold">Projects</h1>
          <p className="text-sm text-muted-foreground">
            Manage projects and their ordered tasks.
          </p>
        </div>

        <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
          <Button
            variant="outline"
            onClick={() => projectsQuery.refetch()}
            disabled={projectsQuery.isFetching}
          >
            Refresh
          </Button>

          <Dialog open={createOpen} onOpenChange={setCreateOpen}>
            <DialogTrigger asChild>
              <Button>Create project</Button>
            </DialogTrigger>

            <DialogContent>
              <DialogHeader>
                <UiDialogTitle>Create project</UiDialogTitle>
                <UiDialogDescription>
                  Create a project to group ordered tasks.
                </UiDialogDescription>
              </DialogHeader>

              <div className="grid gap-4">
                <div className="grid gap-2">
                  <Label htmlFor="name">Name</Label>
                  <Input
                    id="name"
                    value={name}
                    onChange={(e) => setName(e.target.value)}
                    placeholder="demo-project"
                  />
                </div>

                <div className="grid gap-2">
                  <Label htmlFor="description">Description</Label>
                  <Textarea
                    id="description"
                    value={description}
                    onChange={(e) => setDescription(e.target.value)}
                    placeholder="A project groups ordered tasks."
                    rows={4}
                  />
                </div>

                <label className="flex items-center gap-2 text-sm">
                  <input
                    type="checkbox"
                    checked={enable}
                    onChange={(e) => setEnable(e.target.checked)}
                  />
                  Enable
                </label>
              </div>

              <DialogFooter>
                <DialogClose asChild>
                  <Button type="button" variant="outline">
                    Cancel
                  </Button>
                </DialogClose>
                <Button
                  type="button"
                  onClick={() =>
                    createProjectMutation.mutate({
                      name,
                      description: description.length ? description : null,
                      enable,
                    })
                  }
                  disabled={!name.trim() || createProjectMutation.isPending}
                >
                  {createProjectMutation.isPending ? "Creating..." : "Create"}
                </Button>
              </DialogFooter>
            </DialogContent>
          </Dialog>
        </div>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Projects</CardTitle>
          <CardDescription>
            Notes: backend currently filters by <code>Enable</code> in repository
            queries, so disabled projects may not appear in this list.
          </CardDescription>
        </CardHeader>
        <CardContent>
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
              {projects.map((p) => (
                <div
                  key={p.id}
                  className="flex flex-col gap-3 rounded-lg border p-4 sm:flex-row sm:items-start sm:justify-between"
                >
                  <div className="min-w-0 space-y-1">
                    <div className="flex min-w-0 flex-wrap items-center gap-2">
                      <Link
                        href={`/projects/${p.id}`}
                        className="truncate font-medium underline-offset-4 hover:underline"
                      >
                        {p.name}
                      </Link>
                      <span
                        className={
                          p.enable
                            ? "rounded-md bg-emerald-500/10 px-2 py-0.5 text-xs text-emerald-700 dark:text-emerald-300"
                            : "rounded-md bg-muted px-2 py-0.5 text-xs text-muted-foreground"
                        }
                      >
                        {p.enable ? "Enabled" : "Disabled"}
                      </span>
                    </div>

                    {p.description ? (
                      <div className="text-sm text-muted-foreground">
                        {p.description}
                      </div>
                    ) : null}

                    <div className="text-xs text-muted-foreground">
                      <span className="font-mono">{p.id}</span>
                      <span className="mx-2">·</span>
                      Updated: {formatDate(p.updateTime ?? p.createTime)}
                    </div>
                  </div>

                  <div className="flex flex-wrap gap-2 sm:justify-end">
                    <Button asChild variant="outline" size="sm">
                      <Link href={`/projects/${p.id}`}>View</Link>
                    </Button>
                    <Button
                      variant="outline"
                      size="sm"
                      onClick={() => openEdit(p)}
                    >
                      Edit
                    </Button>
                    <Button
                      variant="destructive"
                      size="sm"
                      onClick={() => openDelete(p)}
                      disabled={deleteProjectMutation.isPending}
                    >
                      Delete
                    </Button>
                  </div>
                </div>
              ))}
            </div>
          )}
        </CardContent>
      </Card>

      <Dialog open={editOpen} onOpenChange={setEditOpen}>
        <DialogContent>
          <DialogHeader>
            <UiDialogTitle>Edit project</UiDialogTitle>
            <UiDialogDescription>Update project settings.</UiDialogDescription>
          </DialogHeader>

          <div className="grid gap-4">
            <div className="grid gap-2">
              <Label htmlFor="edit-name">Name</Label>
              <Input
                id="edit-name"
                value={editName}
                onChange={(e) => setEditName(e.target.value)}
              />
            </div>

            <div className="grid gap-2">
              <Label htmlFor="edit-description">Description</Label>
              <Textarea
                id="edit-description"
                value={editDescription}
                onChange={(e) => setEditDescription(e.target.value)}
                rows={4}
              />
            </div>

            <label className="flex items-center gap-2 text-sm">
              <input
                type="checkbox"
                checked={editEnable}
                onChange={(e) => setEditEnable(e.target.checked)}
              />
              Enable
            </label>
          </div>

          <DialogFooter>
            <DialogClose asChild>
              <Button type="button" variant="outline">
                Cancel
              </Button>
            </DialogClose>
            <Button
              type="button"
              onClick={() => {
                if (!editProjectId) return
                updateProjectMutation.mutate({
                  id: editProjectId,
                  body: {
                    name: editName,
                    description: editDescription.length ? editDescription : null,
                    enable: editEnable,
                  },
                })
              }}
              disabled={
                !editProjectId ||
                !editName.trim() ||
                updateProjectMutation.isPending
              }
            >
              {updateProjectMutation.isPending ? "Saving..." : "Save"}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <Dialog open={deleteOpen} onOpenChange={setDeleteOpen}>
        <DialogContent>
          <DialogHeader>
            <UiDialogTitle>Delete project</UiDialogTitle>
            <UiDialogDescription>
              This will permanently delete the project and its tasks (cascade
              delete).
            </UiDialogDescription>
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
                if (!deleteProject) return
                deleteProjectMutation.mutate(deleteProject.id)
              }}
              disabled={!deleteProject || deleteProjectMutation.isPending}
            >
              {deleteProjectMutation.isPending ? "Deleting..." : "Delete"}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  )
}