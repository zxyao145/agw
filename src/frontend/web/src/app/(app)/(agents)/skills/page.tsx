"use client";

import * as React from "react";
import {
  useMutation,
  useQuery,
  useQueryClient,
  type UseMutationResult,
} from "@tanstack/react-query";
import { Pencil, Plus, Trash2 } from "lucide-react";
import { toast } from "sonner";

import { apiDelete, apiGet, apiPost, apiPut } from "@/api/client";
import { StaticTable } from "@/components/static-table";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Empty } from "@/components/ui/empty";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { Textarea } from "@/components/ui/textarea";

import { getApiErrorMessage } from "../agents/components/utils";

type SkillDto = {
  id: string;
  name: string;
  description: string;
  contentPath: string;
  agentIds: string[];
  createTime?: string | null;
  createBy?: string | null;
  updateTime?: string | null;
  updateBy?: string | null;
};

type SkillFormState = {
  name: string;
  description: string;
  archive: File | null;
};

type SkillDialogProps = {
  mode: "create" | "edit";
  open: boolean;
  onOpenChange: (open: boolean) => void;
  form: SkillFormState;
  setForm: React.Dispatch<React.SetStateAction<SkillFormState>>;
  onSubmit: () => void;
  isSubmitting: boolean;
  fileInputKey: number;
  currentSkill?: SkillDto | null;
};

const SKILL_NAME_REGEX = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;

function createDefaultFormState(): SkillFormState {
  return {
    name: "",
    description: "",
    archive: null,
  };
}

function createEditFormState(skill: SkillDto): SkillFormState {
  return {
    name: skill.name,
    description: skill.description,
    archive: null,
  };
}

function formatDateTime(value?: string | null): string {
  if (!value) return "-";

  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;

  return new Intl.DateTimeFormat(undefined, {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(date);
}

function buildDefaultSkillNameFromFileName(fileName: string): string {
  return fileName
    .replace(/\.zip$/i, "")
    .trim()
    .toLowerCase()
    .replace(/[_\s]+/g, "-")
    .replace(/[^a-z0-9-]+/g, "-")
    .replace(/-+/g, "-")
    .replace(/^-+|-+$/g, "");
}

function validateSkillForm(
  form: SkillFormState,
  mode: "create" | "edit",
  currentSkill?: SkillDto | null,
): void {
  const name = form.name.trim();
  const description = form.description.trim();

  if (!name) {
    throw new Error("Skill name is required.");
  }

  if (name.length > 64) {
    throw new Error("Skill name must be 64 characters or fewer.");
  }

  if (!SKILL_NAME_REGEX.test(name)) {
    throw new Error(
      "Skill name must contain only lowercase letters, numbers, and single hyphens.",
    );
  }

  if (!description) {
    throw new Error("Skill description is required.");
  }

  if (description.length > 1024) {
    throw new Error("Skill description must be 1024 characters or fewer.");
  }

  if (mode === "create" && !form.archive) {
    throw new Error("Skill archive is required.");
  }

  if (
    mode === "edit" &&
    currentSkill &&
    currentSkill.name !== name &&
    !form.archive
  ) {
    throw new Error("Renaming a skill requires uploading a new zip archive.");
  }

  if (form.archive && !form.archive.name.toLowerCase().endsWith(".zip")) {
    throw new Error("Skill archive must be a .zip file.");
  }
}

function buildSkillFormData(
  form: SkillFormState,
  mode: "create" | "edit",
  currentSkill?: SkillDto | null,
): FormData {
  validateSkillForm(form, mode, currentSkill);

  const data = new FormData();
  data.append("Name", form.name.trim());
  data.append("Description", form.description.trim());

  if (form.archive) {
    data.append("Archive", form.archive);
  }

  if (mode === "edit" && currentSkill) {
    for (const agentId of Array.from(new Set(currentSkill.agentIds ?? []))) {
      data.append("AgentIds", agentId);
    }
  }

  return data;
}

export default function SkillsPage() {
  const queryClient = useQueryClient();
  const [createOpen, setCreateOpen] = React.useState(false);
  const [editOpen, setEditOpen] = React.useState(false);
  const [deleteOpen, setDeleteOpen] = React.useState(false);
  const [createForm, setCreateForm] = React.useState<SkillFormState>(
    createDefaultFormState,
  );
  const [editForm, setEditForm] = React.useState<SkillFormState>(
    createDefaultFormState,
  );
  const [editingSkill, setEditingSkill] = React.useState<SkillDto | null>(null);
  const [deletingSkill, setDeletingSkill] = React.useState<SkillDto | null>(
    null,
  );
  const [createFileInputKey, setCreateFileInputKey] = React.useState(0);
  const [editFileInputKey, setEditFileInputKey] = React.useState(0);

  const skillsQuery = useQuery({
    queryKey: ["skills"],
    queryFn: async () => {
      return (await apiGet("/api/skills")) as unknown as SkillDto[];
    },
  });

  const createMutation = useMutation({
    mutationFn: async (body: FormData) => {
      return await apiPost("/api/skills", { body });
    },
    onSuccess: async () => {
      toast.success("Skill created");
      setCreateOpen(false);
      setCreateForm(createDefaultFormState());
      setCreateFileInputKey((value) => value + 1);
      await queryClient.invalidateQueries({ queryKey: ["skills"] });
    },
    onError: (error) => {
      toast.error(`Create failed: ${getApiErrorMessage(error)}`);
    },
  });

  const updateMutation = useMutation({
    mutationFn: async ({ id, body }: { id: string; body: FormData }) => {
      return await apiPut("/api/skills/{id}", {
        params: { path: { id } },
        body,
      });
    },
    onSuccess: async () => {
      toast.success("Skill updated");
      setEditOpen(false);
      setEditingSkill(null);
      setEditForm(createDefaultFormState());
      setEditFileInputKey((value) => value + 1);
      await queryClient.invalidateQueries({ queryKey: ["skills"] });
    },
    onError: (error) => {
      toast.error(`Update failed: ${getApiErrorMessage(error)}`);
    },
  });

  const deleteMutation = useMutation({
    mutationFn: async (id: string) => {
      return await apiDelete("/api/skills/{id}", {
        params: { path: { id } },
      });
    },
    onSuccess: async () => {
      toast.success("Skill deleted");
      setDeleteOpen(false);
      setDeletingSkill(null);
      await queryClient.invalidateQueries({ queryKey: ["skills"] });
    },
    onError: (error) => {
      toast.error(`Delete failed: ${getApiErrorMessage(error)}`);
    },
  });

  const openEditDialog = (skill: SkillDto) => {
    setEditingSkill(skill);
    setEditForm(createEditFormState(skill));
    setEditFileInputKey((value) => value + 1);
    setEditOpen(true);
  };

  const closeCreateDialog = (open: boolean) => {
    setCreateOpen(open);
    if (!open && !createMutation.isPending) {
      setCreateForm(createDefaultFormState());
      setCreateFileInputKey((value) => value + 1);
    }
  };

  const closeEditDialog = (open: boolean) => {
    setEditOpen(open);
    if (!open && !updateMutation.isPending) {
      setEditingSkill(null);
      setEditForm(createDefaultFormState());
      setEditFileInputKey((value) => value + 1);
    }
  };

  const closeDeleteDialog = (open: boolean) => {
    setDeleteOpen(open);
    if (!open && !deleteMutation.isPending) {
      setDeletingSkill(null);
    }
  };

  const submitCreate = () => {
    try {
      createMutation.mutate(buildSkillFormData(createForm, "create"));
    } catch (error) {
      toast.error(getApiErrorMessage(error));
    }
  };

  const submitEdit = () => {
    if (!editingSkill) return;

    try {
      updateMutation.mutate({
        id: editingSkill.id,
        body: buildSkillFormData(editForm, "edit", editingSkill),
      });
    } catch (error) {
      toast.error(getApiErrorMessage(error));
    }
  };

  const handleDelete = (skill: SkillDto) => {
    setDeletingSkill(skill);
    setDeleteOpen(true);
  };

  return (
    <div className="space-y-6 w-full">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div className="min-w-0">
          <h1 className="truncate text-xl font-semibold">Skills</h1>
          <p className="text-sm text-muted-foreground">
            Upload, assign, update, and remove reusable skills for agents.
          </p>
        </div>

        <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
          <Button
            variant="outline"
            onClick={() => {
              skillsQuery.refetch();
            }}
            disabled={skillsQuery.isFetching}
          >
            Refresh
          </Button>

          <Button onClick={() => setCreateOpen(true)}>
            <Plus className="mr-2 h-4 w-4" />
            Add Skill
          </Button>
        </div>
      </div>

      {skillsQuery.isLoading ? (
        <div className="text-sm text-muted-foreground">Loading...</div>
      ) : skillsQuery.isError ? (
        <div className="text-sm text-destructive">
          Failed to load skills: {getApiErrorMessage(skillsQuery.error)}
        </div>
      ) : (
        <StaticTable
          isEmpty={
            skillsQuery.data === undefined || skillsQuery.data.length === 0
          }
        >
          <Empty>
            <div className="text-sm text-muted-foreground">
              No skills found. Upload a skill archive to get started.
            </div>
          </Empty>
          <TableHeader>
            <TableRow>
              <TableHead>Name</TableHead>
              <TableHead>Description</TableHead>
              <TableHead>Content Path</TableHead>
              <TableHead>Updated</TableHead>
              <TableHead className="w-32 text-right">Actions</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {skillsQuery.data?.map((skill) => (
              <TableRow key={skill.id}>
                <TableCell className="min-w-44">
                  <div className="font-medium">{skill.name}</div>
                  <div className="text-xs text-muted-foreground">
                    Created {formatDateTime(skill.createTime)}
                  </div>
                </TableCell>
                <TableCell className="max-w-md">
                  <div className="line-clamp-3 text-sm text-muted-foreground">
                    {skill.description}
                  </div>
                </TableCell>
                <TableCell className="max-w-sm font-mono text-xs break-all text-muted-foreground">
                  {skill.contentPath}
                </TableCell>
                <TableCell className="min-w-40 text-sm text-muted-foreground">
                  {formatDateTime(skill.updateTime ?? skill.createTime)}
                </TableCell>
                <TableCell className="text-right">
                  <div className="flex justify-end gap-2">
                    <Button
                      type="button"
                      variant="outline"
                      size="sm"
                      onClick={() => openEditDialog(skill)}
                    >
                      <Pencil className="h-4 w-4" />
                    </Button>
                    <Button
                      type="button"
                      variant="destructive"
                      size="sm"
                      onClick={() => handleDelete(skill)}
                      disabled={deleteMutation.isPending}
                    >
                      <Trash2 className="h-4 w-4" />
                    </Button>
                  </div>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </StaticTable>
      )}

      <SkillDialog
        mode="create"
        open={createOpen}
        onOpenChange={closeCreateDialog}
        form={createForm}
        setForm={setCreateForm}
        onSubmit={submitCreate}
        isSubmitting={createMutation.isPending}
        fileInputKey={createFileInputKey}
      />

      <SkillDialog
        mode="edit"
        open={editOpen}
        onOpenChange={closeEditDialog}
        form={editForm}
        setForm={setEditForm}
        onSubmit={submitEdit}
        isSubmitting={updateMutation.isPending}
        fileInputKey={editFileInputKey}
        currentSkill={editingSkill}
      />

      <DeleteSkillDialog
        open={deleteOpen}
        onOpenChange={closeDeleteDialog}
        deletingSkill={deletingSkill}
        deleteMutation={deleteMutation}
      />
    </div>
  );
}

function DeleteSkillDialog({
  open,
  onOpenChange,
  deletingSkill,
  deleteMutation,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  deletingSkill: SkillDto | null;
  deleteMutation: UseMutationResult<unknown, Error, string, unknown>;
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Delete skill</DialogTitle>
          <DialogDescription>
            Are you sure you want to delete skill &quot;{deletingSkill?.name}
            &quot;? This will remove its database record and skill directory.
          </DialogDescription>
        </DialogHeader>

        <DialogFooter>
          <Button
            type="button"
            variant="outline"
            onClick={() => onOpenChange(false)}
          >
            Cancel
          </Button>
          <Button
            type="button"
            variant="destructive"
            onClick={() => {
              if (deletingSkill) {
                deleteMutation.mutate(deletingSkill.id);
              }
            }}
            disabled={deleteMutation.isPending}
          >
            {deleteMutation.isPending ? "Deleting..." : "Delete"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function SkillDialog({
  mode,
  open,
  onOpenChange,
  form,
  setForm,
  onSubmit,
  isSubmitting,
  fileInputKey,
  currentSkill,
}: SkillDialogProps) {
  const title = mode === "create" ? "Create Skill" : "Edit Skill";
  const submitLabel = mode === "create" ? "Create Skill" : "Save Changes";

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-4xl max-h-[90vh] overflow-hidden">
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
          <DialogDescription>
            {mode === "create"
              ? "Upload a zipped skill package. Agent associations are managed from the Agents page."
              : "Update skill metadata and archive contents. Agent associations are managed from the Agents page."}
          </DialogDescription>
        </DialogHeader>

        <div className="grid grid-cols-1 gap-6 overflow-y-auto pr-1 sm:grid-cols-2">
          <div className="space-y-2 sm:col-span-2">
            <Label htmlFor={`${mode}-skill-archive`}>
              Archive {mode === "create" ? "(Required)" : "(Optional)"}
            </Label>
            <p className="text-xs text-muted-foreground">
              {mode === "create"
                ? "Upload a .zip file containing a skill directory with SKILL.md."
                : "Leave empty to keep the current archive. Upload a new .zip if you rename the skill."}
            </p>

            <Input
              key={fileInputKey}
              id={`${mode}-skill-archive`}
              type="file"
              accept=".zip,application/zip"
              onChange={(event) =>
                setForm((current) => {
                  const archive = event.target.files?.[0] ?? null;
                  if (mode !== "create" || !archive) {
                    return {
                      ...current,
                      archive,
                    };
                  }

                  const previousDefaultName = current.archive
                    ? buildDefaultSkillNameFromFileName(current.archive.name)
                    : "";
                  const nextDefaultName = buildDefaultSkillNameFromFileName(
                    archive.name,
                  );
                  const shouldUpdateName =
                    !current.name.trim() ||
                    current.name === previousDefaultName;

                  return {
                    ...current,
                    archive,
                    name:
                      shouldUpdateName && nextDefaultName
                        ? nextDefaultName
                        : current.name,
                  };
                })
              }
            />
          </div>

          <div className="space-y-2 sm:col-span-2">
            <Label htmlFor={`${mode}-skill-name`}>Name</Label>
            <p className="text-xs text-muted-foreground">
              Lowercase letters, numbers, and single hyphens only.
            </p>
            <Input
              id={`${mode}-skill-name`}
              value={form.name}
              placeholder="example-skill"
              onChange={(event) =>
                setForm((current) => ({
                  ...current,
                  name: event.target.value,
                }))
              }
              disabled={mode === "edit"}
            />
          </div>

          <div className="space-y-2 sm:col-span-2">
            <Label htmlFor={`${mode}-skill-description`}>Description</Label>
            <p className="text-xs text-muted-foreground">
              If the frontmatter in SKILL.md does not include a "description"
              field, the value of this field will be used.
            </p>
            <Textarea
              id={`${mode}-skill-description`}
              rows={4}
              value={form.description}
              placeholder="Describe what this skill provides."
              onChange={(event) =>
                setForm((current) => ({
                  ...current,
                  description: event.target.value,
                }))
              }
            />
          </div>

          {mode === "edit" && currentSkill ? (
            <div className="space-y-2 sm:col-span-2">
              <Label>Current Content Path</Label>
              <div className="rounded-md border bg-muted/30 px-3 py-2 font-mono text-xs break-all text-muted-foreground">
                {currentSkill.contentPath}
              </div>
            </div>
          ) : null}
        </div>

        <DialogFooter>
          <Button
            type="button"
            variant="outline"
            onClick={() => onOpenChange(false)}
            disabled={isSubmitting}
          >
            Cancel
          </Button>
          <Button type="button" onClick={onSubmit} disabled={isSubmitting}>
            {isSubmitting ? "Saving..." : submitLabel}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
