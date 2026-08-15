"use client";

import * as React from "react";
import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@agw/components/query";
import { BookOpenText, Pencil, Plus, RefreshCw, Trash2 } from "lucide-react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import { toast } from "sonner";

import { apiDelete, apiGet, apiPost, apiPut, getApiErrorMessage } from "@agw/api";
import {
  Badge,
  Button,
  ButtonGroup,
  DEFAULT_PAGE_SIZE,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  Empty,
  Input,
  Label,
  PaginatedTable,
  StaticTable,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
  Textarea,
  formatLocalDateTime,
  getClampedPageIndex,
  type PagedResult,
} from "@agw/components";

type UserMemorySummary = {
  id: string;
  name: string;
  description?: string | null;
  createTime: string;
  updateTime?: string | null;
};

type UserMemoryDetail = UserMemorySummary & {
  content: string;
};

type UserMemoryForm = {
  name: string;
  description: string;
  content: string;
};

const EMPTY_FORM: UserMemoryForm = {
  name: "",
  description: "",
  content: "",
};

export function validateMemoryForm(form: UserMemoryForm): UserMemoryForm {
  const name = form.name.trim();
  const description = form.description.trim();
  if (!name) throw new Error("Memory name is required.");
  if (name.length > 64) throw new Error("Memory name must be 64 characters or fewer.");
  if (description.length > 300) {
    throw new Error("Description must be 300 characters or fewer.");
  }
  if (!form.content.trim()) throw new Error("Memory content is required.");

  return { name, description, content: form.content };
}

export function UserMemoryPage() {
  const queryClient = useQueryClient();
  const [pageIndex, setPageIndex] = React.useState(1);
  const [pageSize, setPageSize] = React.useState(DEFAULT_PAGE_SIZE);
  const [createOpen, setCreateOpen] = React.useState(false);
  const [editOpen, setEditOpen] = React.useState(false);
  const [deleteOpen, setDeleteOpen] = React.useState(false);
  const [createForm, setCreateForm] = React.useState<UserMemoryForm>(EMPTY_FORM);
  const [editForm, setEditForm] = React.useState<UserMemoryForm>(EMPTY_FORM);
  const [editingMemory, setEditingMemory] = React.useState<UserMemorySummary | null>(null);
  const [deletingMemory, setDeletingMemory] = React.useState<UserMemorySummary | null>(null);

  const memoriesQuery = useQuery({
    queryKey: ["user-memories", "paged", pageIndex, pageSize],
    queryFn: async () =>
      (await apiGet("/api/user-memories/paged", {
        params: { query: { pageIndex, pageSize } },
      })) as PagedResult<UserMemorySummary>,
    placeholderData: keepPreviousData,
  });
  const detailQuery = useQuery({
    queryKey: ["user-memories", "detail", editingMemory?.id],
    queryFn: async () =>
      (await apiGet("/api/user-memories/detail", {
        params: { query: { id: editingMemory!.id } },
      })) as UserMemoryDetail,
    enabled: editOpen && Boolean(editingMemory),
  });

  const memories = memoriesQuery.data?.items ?? [];
  const total = Number(memoriesQuery.data?.total ?? 0);

  React.useEffect(() => {
    if (!memoriesQuery.data) return;
    const clamped = getClampedPageIndex(total, pageIndex, pageSize);
    if (clamped !== pageIndex) setPageIndex(clamped);
  }, [memoriesQuery.data, pageIndex, pageSize, total]);

  React.useEffect(() => {
    const detail = detailQuery.data;
    if (!detail || detail.id !== editingMemory?.id) return;
    setEditForm({
      name: detail.name,
      description: detail.description ?? "",
      content: detail.content,
    });
  }, [detailQuery.data, editingMemory?.id]);

  const createMutation = useMutation({
    mutationFn: async (form: UserMemoryForm) => {
      const normalized = validateMemoryForm(form);
      return await apiPost("/api/user-memories", {
        body: {
          name: normalized.name,
          description: normalized.description || null,
          content: normalized.content,
        },
      });
    },
    onSuccess: async () => {
      toast.success("Memory created");
      setCreateOpen(false);
      setCreateForm(EMPTY_FORM);
      setPageIndex(1);
      await queryClient.invalidateQueries({ queryKey: ["user-memories"] });
    },
    onError: (error) => toast.error(`Create failed: ${getApiErrorMessage(error)}`),
  });

  const updateMutation = useMutation({
    mutationFn: async ({ id, form }: { id: string; form: UserMemoryForm }) => {
      const normalized = validateMemoryForm(form);
      return await apiPut("/api/user-memories", {
        body: {
          id,
          name: normalized.name,
          description: normalized.description || null,
          content: normalized.content,
        },
      });
    },
    onSuccess: async () => {
      toast.success("Memory updated");
      setEditOpen(false);
      setEditingMemory(null);
      setEditForm(EMPTY_FORM);
      await queryClient.invalidateQueries({ queryKey: ["user-memories"] });
    },
    onError: (error) => toast.error(`Update failed: ${getApiErrorMessage(error)}`),
  });

  const deleteMutation = useMutation({
    mutationFn: async (id: string) =>
      await apiDelete("/api/user-memories", { params: { query: { id } } }),
    onSuccess: async () => {
      toast.success("Memory deleted");
      setDeleteOpen(false);
      setDeletingMemory(null);
      setPageIndex(getClampedPageIndex(Math.max(0, total - 1), pageIndex, pageSize));
      await queryClient.invalidateQueries({ queryKey: ["user-memories"] });
    },
    onError: (error) => toast.error(`Delete failed: ${getApiErrorMessage(error)}`),
  });

  const openEdit = (memory: UserMemorySummary) => {
    setEditingMemory(memory);
    setEditForm({
      name: memory.name,
      description: memory.description ?? "",
      content: "",
    });
    setEditOpen(true);
  };

  return (
    <div className="w-full space-y-6">
      <header className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
        <div className="flex min-w-0 items-start gap-3">
          <span className="grid size-10 shrink-0 place-items-center rounded-xl border bg-muted/40 text-foreground shadow-sm">
            <BookOpenText className="size-5" />
          </span>
          <div className="min-w-0">
            <div className="flex flex-wrap items-center gap-2">
              <h1 className="truncate text-xl font-semibold">User Memory</h1>
              <Badge variant="secondary">Private · Database</Badge>
            </div>
            <p className="mt-1 text-sm text-muted-foreground">
              Keep personal context and preferences available across every project.
            </p>
          </div>
        </div>
        <div className="flex gap-2">
          <Button
            variant="outline"
            onClick={() => memoriesQuery.refetch()}
            disabled={memoriesQuery.isFetching}
          >
            <RefreshCw className={memoriesQuery.isFetching ? "animate-spin" : ""} />
            Refresh
          </Button>
          <Button onClick={() => setCreateOpen(true)}>
            <Plus />
            Add Memory
          </Button>
        </div>
      </header>

      {memoriesQuery.isLoading ? (
        <p className="text-sm text-muted-foreground">Loading memories...</p>
      ) : memoriesQuery.isError ? (
        <p className="text-sm text-destructive">
          Failed to load memories: {getApiErrorMessage(memoriesQuery.error)}
        </p>
      ) : (
        <PaginatedTable
          pageIndex={pageIndex}
          pageSize={pageSize}
          total={total}
          isFetching={memoriesQuery.isFetching}
          onPageIndexChange={setPageIndex}
          onPageSizeChange={(value) => {
            setPageSize(value);
            setPageIndex(1);
          }}
        >
          <StaticTable embedded isEmpty={memories.length === 0}>
            <Empty>
              <div className="text-sm text-muted-foreground">
                No user memories yet. Add the first note you want agents to remember.
              </div>
            </Empty>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Description</TableHead>
                <TableHead>Updated</TableHead>
                <TableHead className="w-28 text-right">Actions</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {memories.map((memory) => (
                <TableRow key={memory.id}>
                  <TableCell className="min-w-48">
                    <div className="font-medium">{memory.name}</div>
                    <div className="mt-0.5 text-xs text-muted-foreground">
                      Created {formatLocalDateTime(memory.createTime)}
                    </div>
                  </TableCell>
                  <TableCell className="max-w-xl">
                    <p className="line-clamp-2 text-sm text-muted-foreground">
                      {memory.description || "No description"}
                    </p>
                  </TableCell>
                  <TableCell className="min-w-40 text-sm text-muted-foreground">
                    {formatLocalDateTime(memory.updateTime ?? memory.createTime)}
                  </TableCell>
                  <TableCell className="text-right">
                    <ButtonGroup>
                      <Button
                        variant="ghost"
                        size="icon-sm"
                        aria-label={`Edit ${memory.name}`}
                        onClick={() => openEdit(memory)}
                      >
                        <Pencil />
                      </Button>
                      <Button
                        variant="ghost"
                        size="icon-sm"
                        aria-label={`Delete ${memory.name}`}
                        className="text-destructive hover:bg-destructive/10 hover:text-destructive"
                        onClick={() => {
                          setDeletingMemory(memory);
                          setDeleteOpen(true);
                        }}
                      >
                        <Trash2 />
                      </Button>
                    </ButtonGroup>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </StaticTable>
        </PaginatedTable>
      )}

      <MemoryDialog
        mode="create"
        open={createOpen}
        onOpenChange={(open) => {
          setCreateOpen(open);
          if (!open && !createMutation.isPending) setCreateForm(EMPTY_FORM);
        }}
        form={createForm}
        setForm={setCreateForm}
        onSave={() => createMutation.mutate(createForm)}
        isSaving={createMutation.isPending}
      />
      <MemoryDialog
        mode="edit"
        open={editOpen}
        onOpenChange={(open) => {
          setEditOpen(open);
          if (!open && !updateMutation.isPending) {
            setEditingMemory(null);
            setEditForm(EMPTY_FORM);
          }
        }}
        form={editForm}
        setForm={setEditForm}
        onSave={() => {
          if (editingMemory) updateMutation.mutate({ id: editingMemory.id, form: editForm });
        }}
        isSaving={updateMutation.isPending}
        isLoading={detailQuery.isLoading || detailQuery.isFetching}
        error={detailQuery.isError ? getApiErrorMessage(detailQuery.error) : null}
      />
      <Dialog open={deleteOpen} onOpenChange={setDeleteOpen}>
        <DialogContent size="sm">
          <DialogHeader>
            <DialogTitle>Delete memory</DialogTitle>
            <DialogDescription>
              Delete &quot;{deletingMemory?.name}&quot; permanently? Agents will no longer be able
              to read it.
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="outline" onClick={() => setDeleteOpen(false)}>
              Cancel
            </Button>
            <Button
              variant="destructive"
              disabled={!deletingMemory || deleteMutation.isPending}
              onClick={() => deletingMemory && deleteMutation.mutate(deletingMemory.id)}
            >
              {deleteMutation.isPending ? "Deleting..." : "Delete"}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}

function MemoryDialog({
  mode,
  open,
  onOpenChange,
  form,
  setForm,
  onSave,
  isSaving,
  isLoading = false,
  error,
}: {
  mode: "create" | "edit";
  open: boolean;
  onOpenChange: (open: boolean) => void;
  form: UserMemoryForm;
  setForm: React.Dispatch<React.SetStateAction<UserMemoryForm>>;
  onSave: () => void;
  isSaving: boolean;
  isLoading?: boolean;
  error?: string | null;
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent
        size="3xl"
        className="max-h-[calc(100vh-4rem)] grid-rows-[auto_minmax(0,1fr)_auto] overflow-hidden"
      >
        <DialogHeader>
          <DialogTitle>{mode === "create" ? "Add user memory" : "Edit user memory"}</DialogTitle>
          <DialogDescription>
            Content is saved as Markdown and remains private to the current user.
          </DialogDescription>
        </DialogHeader>

        {isLoading ? (
          <div className="grid min-h-80 place-items-center text-sm text-muted-foreground">
            Loading memory content...
          </div>
        ) : error ? (
          <div className="grid min-h-80 place-items-center text-sm text-destructive">{error}</div>
        ) : (
          <div className="min-h-0 space-y-4 overflow-y-auto pr-1 agw-scrollbar">
            <div className="grid gap-4 md:grid-cols-[minmax(0,0.9fr)_minmax(0,1.6fr)]">
              <div className="space-y-2">
                <div className="flex items-center justify-between gap-3">
                  <Label htmlFor={`${mode}-memory-name`}>Name</Label>
                  <span className="text-xs tabular-nums text-muted-foreground">
                    {form.name.length}/64
                  </span>
                </div>
                <Input
                  id={`${mode}-memory-name`}
                  maxLength={64}
                  value={form.name}
                  placeholder="Writing preferences"
                  onChange={(event) =>
                    setForm((current) => ({ ...current, name: event.target.value }))
                  }
                />
              </div>
              <div className="space-y-2">
                <div className="flex items-center justify-between gap-3">
                  <Label htmlFor={`${mode}-memory-description`}>Description</Label>
                  <span className="text-xs tabular-nums text-muted-foreground">
                    {form.description.length}/300
                  </span>
                </div>
                <Input
                  id={`${mode}-memory-description`}
                  maxLength={300}
                  value={form.description}
                  placeholder="A short hint that helps agents discover this memory"
                  onChange={(event) =>
                    setForm((current) => ({ ...current, description: event.target.value }))
                  }
                />
              </div>
            </div>

            <Tabs defaultValue="edit" className="min-h-0">
              <div className="flex items-center justify-between gap-3 border-b pb-2">
                <TabsList>
                  <TabsTrigger value="edit">Edit</TabsTrigger>
                  <TabsTrigger value="preview">Preview</TabsTrigger>
                </TabsList>
                <span className="text-xs tabular-nums text-muted-foreground">
                  {form.content.length.toLocaleString()} characters
                </span>
              </div>
              <TabsContent value="edit" className="mt-3">
                <Textarea
                  aria-label="Memory content"
                  className="min-h-[42vh] resize-y font-mono text-sm leading-6"
                  value={form.content}
                  placeholder={`### What agents should remember

- Your preferred language, tone, and response style
- Stable personal context that helps agents assist you
- Instructions that should apply across conversations`}
                  onChange={(event) =>
                    setForm((current) => ({ ...current, content: event.target.value }))
                  }
                />
              </TabsContent>
              <TabsContent value="preview" className="mt-3">
                <div className="min-h-[42vh] overflow-auto rounded-lg border bg-muted/20 p-5 agw-scrollbar">
                  {form.content.trim() ? (
                    <article className="prose prose-sm max-w-none break-words">
                      <ReactMarkdown remarkPlugins={[remarkGfm]}>{form.content}</ReactMarkdown>
                    </article>
                  ) : (
                    <p className="text-sm text-muted-foreground">Nothing to preview yet.</p>
                  )}
                </div>
              </TabsContent>
            </Tabs>
          </div>
        )}

        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)} disabled={isSaving}>
            Cancel
          </Button>
          <Button onClick={onSave} disabled={isSaving || isLoading || Boolean(error)}>
            {isSaving ? "Saving..." : "Save Memory"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
