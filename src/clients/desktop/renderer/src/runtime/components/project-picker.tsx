"use client";

import * as React from "react";
import { Check, FolderKanban, LoaderCircle, Plus, Search } from "lucide-react";

import { getApiErrorMessage } from "@agw/api";
import { Button, Input, Label } from "@agw/components";
import { Popover, PopoverContent, PopoverTrigger } from "@agw/components";
import { cn } from "@agw/components";
import {
  formatProjectFolderName,
  resolveCreateProjectWorkspace,
  syncDefaultProjectWorkspace,
} from "@agw/projects";

export type DesktopProjectOption = {
  id: string;
  name: string;
  workspace?: string | null;
};

export type DesktopProjectCreateInput = {
  name: string;
  workspace: string | null;
};

type DesktopProjectPickerProps = {
  projects: DesktopProjectOption[];
  activeProjectId: string;
  isLoading: boolean;
  errorMessage: string | null;
  onSelect(projectId: string): void;
  onCreate(input: DesktopProjectCreateInput): Promise<void>;
};

export function DesktopProjectPicker({
  projects,
  activeProjectId,
  isLoading,
  errorMessage,
  onSelect,
  onCreate,
}: DesktopProjectPickerProps) {
  const [open, setOpen] = React.useState(false);
  const [search, setSearch] = React.useState("");
  const [createMode, setCreateMode] = React.useState(false);
  const [createName, setCreateName] = React.useState("");
  const [createWorkspace, setCreateWorkspace] = React.useState("");
  const [createError, setCreateError] = React.useState<string | null>(null);
  const [isCreating, setIsCreating] = React.useState(false);
  const searchInputRef = React.useRef<HTMLInputElement | null>(null);
  const createNameInputRef = React.useRef<HTMLInputElement | null>(null);

  React.useEffect(() => {
    if (!open) return;
    const timeout = window.setTimeout(() => {
      if (createMode) createNameInputRef.current?.focus();
      else searchInputRef.current?.focus();
    }, 0);
    return () => window.clearTimeout(timeout);
  }, [createMode, open]);

  const filteredProjects = React.useMemo(() => {
    const query = search.trim().toLocaleLowerCase();
    if (!query) return projects;

    return projects.filter((project) => {
      const searchableText = `${project.name} ${project.workspace ?? ""} ${project.id}`;
      return searchableText.toLocaleLowerCase().includes(query);
    });
  }, [projects, search]);

  const normalizedName = formatProjectFolderName(createName);
  const invalidCreateName = createName.trim().length > 0 && !normalizedName;

  const resetCreateForm = () => {
    setCreateName("");
    setCreateWorkspace("");
    setCreateError(null);
  };

  const handleOpenChange = (nextOpen: boolean) => {
    if (!nextOpen && isCreating) return;

    setOpen(nextOpen);
    if (!nextOpen) {
      setSearch("");
      setCreateMode(false);
      resetCreateForm();
    }
  };

  const handleSelect = (projectId: string) => {
    onSelect(projectId);
    setOpen(false);
    setSearch("");
  };

  const handleCreateNameChange = (nextName: string) => {
    setCreateWorkspace((currentWorkspace) =>
      syncDefaultProjectWorkspace({
        previousName: createName,
        nextName,
        currentWorkspace,
      }),
    );
    setCreateName(nextName);
  };

  const handleCancelCreate = () => {
    if (isCreating) return;
    resetCreateForm();
    setCreateMode(false);
  };

  const handleCreate = async (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (!normalizedName || isCreating) return;

    setCreateError(null);
    setIsCreating(true);
    try {
      await onCreate({
        name: normalizedName,
        workspace: resolveCreateProjectWorkspace(normalizedName, createWorkspace),
      });
      resetCreateForm();
      setCreateMode(false);
      setSearch("");
      setOpen(false);
    } catch (error) {
      setCreateError(`Create failed: ${getApiErrorMessage(error)}`);
    } finally {
      setIsCreating(false);
    }
  };

  const handleCreateKeyDown = (event: React.KeyboardEvent<HTMLFormElement>) => {
    if (event.key !== "Escape") return;
    event.preventDefault();
    event.stopPropagation();
    handleCancelCreate();
  };

  const emptyMessage = projects.length === 0 ? "No projects available" : "No matching projects";

  return (
    <Popover open={open} onOpenChange={handleOpenChange}>
      <PopoverTrigger asChild>
        <button type="button" className="agw-titlebar-button" aria-label="Open project">
          <Plus />
        </button>
      </PopoverTrigger>
      <PopoverContent
        align="start"
        sideOffset={8}
        className="w-88 rounded-2xl border-border/70 bg-popover/98 p-0 shadow-2xl shadow-black/12 backdrop-blur-xl"
        onEscapeKeyDown={(event) => {
          if (!createMode) return;
          event.preventDefault();
          handleCancelCreate();
        }}
      >
        <div className="flex items-center justify-between border-b border-border/70 px-4 py-3">
          <div>
            <div className="text-sm font-semibold tracking-tight">Open project</div>
            <div className="mt-0.5 text-xs text-muted-foreground">
              Add a workspace to the title bar
            </div>
          </div>
          <div className="flex items-center gap-1.5">
            <button
              type="button"
              aria-label="Create project"
              title="Create project"
              aria-expanded={createMode}
              disabled={isCreating}
              onClick={() => {
                setCreateError(null);
                setCreateMode(true);
              }}
              className={cn(
                "cursor-pointer grid size-7 place-items-center rounded-lg text-muted-foreground outline-none transition-colors",
                "hover:bg-muted hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring/40",
                "disabled:pointer-events-none disabled:opacity-50",
                createMode && "bg-primary/10 text-primary",
              )}
            >
              <Plus className="size-3.5" />
            </button>
            <span className="rounded-full bg-muted px-2 py-0.5 text-[11px] font-medium text-muted-foreground">
              {projects.length}
            </span>
          </div>
        </div>

        <div className="p-2.5">
          {createMode ? (
            <form
              className="rounded-xl border border-border/70 bg-muted/20 p-3"
              aria-label="Create project"
              onSubmit={handleCreate}
              onKeyDown={handleCreateKeyDown}
            >
              <div className="mb-3">
                <div className="text-sm font-semibold tracking-tight">New project</div>
                <p className="mt-0.5 text-xs text-muted-foreground">
                  Start with a name and workspace.
                </p>
              </div>

              <div className="grid gap-3">
                <div className="grid gap-1.5">
                  <Label htmlFor="desktop-project-name" className="text-xs">
                    Name
                  </Label>
                  <Input
                    id="desktop-project-name"
                    ref={createNameInputRef}
                    value={createName}
                    onChange={(event) => handleCreateNameChange(event.target.value)}
                    placeholder="my-project"
                    autoComplete="off"
                    aria-invalid={invalidCreateName}
                    className="h-9 rounded-xl border-border/70 bg-background text-sm shadow-none"
                  />
                  {invalidCreateName ? (
                    <p className="text-xs text-destructive">Enter a valid project name.</p>
                  ) : null}
                </div>

                <div className="grid gap-1.5">
                  <Label htmlFor="desktop-project-workspace" className="text-xs">
                    Workspace <span className="font-normal text-muted-foreground">(optional)</span>
                  </Label>
                  <Input
                    id="desktop-project-workspace"
                    value={createWorkspace}
                    onChange={(event) => setCreateWorkspace(event.target.value)}
                    placeholder="~/.agw/my-project"
                    autoComplete="off"
                    className="h-9 rounded-xl border-border/70 bg-background font-mono text-xs shadow-none"
                  />
                </div>

                {createError ? (
                  <div
                    role="alert"
                    className="rounded-lg border border-destructive/20 bg-destructive/6 px-2.5 py-2 text-xs text-destructive"
                  >
                    {createError}
                  </div>
                ) : null}

                <div className="flex items-center justify-end gap-2 pt-0.5">
                  <Button
                    type="button"
                    variant="ghost"
                    size="sm"
                    disabled={isCreating}
                    onClick={handleCancelCreate}
                  >
                    Cancel
                  </Button>
                  <Button type="submit" size="sm" disabled={!normalizedName || isCreating}>
                    {isCreating ? <LoaderCircle className="size-3.5 animate-spin" /> : null}
                    {isCreating ? "Creating…" : "Create project"}
                  </Button>
                </div>
              </div>
            </form>
          ) : (
            <>
              <div className="relative">
                <Search className="pointer-events-none absolute left-3 top-1/2 size-3.5 -translate-y-1/2 text-muted-foreground" />
                <Input
                  ref={searchInputRef}
                  value={search}
                  onChange={(event) => setSearch(event.target.value)}
                  onKeyDown={(event) => {
                    event.stopPropagation();
                    if (event.key === "Escape") handleOpenChange(false);
                  }}
                  placeholder="Search projects…"
                  aria-label="Search projects"
                  className="h-9 rounded-xl border-border/70 bg-muted/35 pl-9 text-sm shadow-none focus-visible:bg-background"
                />
              </div>

              <div
                className="mt-2 max-h-80 overflow-y-auto agw-scrollbar"
                role="listbox"
                aria-label="Projects"
              >
                {errorMessage ? (
                  <div className="rounded-xl border border-destructive/20 bg-destructive/6 px-3 py-3 text-sm text-destructive">
                    {errorMessage}
                  </div>
                ) : isLoading ? (
                  <div className="flex items-center gap-2 px-3 py-5 text-sm text-muted-foreground">
                    <LoaderCircle className="size-4 animate-spin" />
                    Loading projects…
                  </div>
                ) : filteredProjects.length === 0 ? (
                  <div className="px-3 py-5 text-center text-sm text-muted-foreground">
                    {emptyMessage}
                  </div>
                ) : (
                  filteredProjects.map((project) => {
                    const active = project.id === activeProjectId;
                    return (
                      <button
                        key={project.id}
                        type="button"
                        role="option"
                        aria-selected={project.id === activeProjectId}
                        className={cn(
                          "group flex w-full items-center gap-3 rounded-xl px-3 py-2.5 text-left outline-none transition-colors",
                          "hover:bg-muted/70 focus-visible:bg-muted focus-visible:ring-2 focus-visible:ring-ring/40",
                          active && "bg-primary/8 text-foreground",
                        )}
                        onClick={() => handleSelect(project.id)}
                      >
                        <span
                          className={cn(
                            "grid size-8 shrink-0 place-items-center rounded-[10px] border bg-background text-muted-foreground",
                            active && "border-primary/25 bg-primary/10 text-primary",
                          )}
                        >
                          <FolderKanban className="size-4" />
                        </span>
                        <span className="min-w-0 flex-1">
                          <span className="block truncate text-sm font-medium">{project.name}</span>
                          <span className="mt-0.5 block truncate text-xs text-muted-foreground">
                            {project.workspace?.trim() || project.id}
                          </span>
                        </span>
                        <span className="grid size-5 shrink-0 place-items-center text-primary">
                          {active ? <Check className="size-4" /> : null}
                        </span>
                      </button>
                    );
                  })
                )}
              </div>
            </>
          )}
        </div>
      </PopoverContent>
    </Popover>
  );
}
