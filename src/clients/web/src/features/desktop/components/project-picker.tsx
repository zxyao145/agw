"use client";

import * as React from "react";
import { Check, FolderKanban, LoaderCircle, Plus, Search } from "lucide-react";

import { Input } from "@/components/ui/input";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { cn } from "@/lib/utils";

export type DesktopProjectOption = {
  id: string;
  name: string;
  workspace?: string | null;
};

type DesktopProjectPickerProps = {
  projects: DesktopProjectOption[];
  activeProjectId: string;
  isLoading: boolean;
  errorMessage: string | null;
  onSelect(projectId: string): void;
};

export function DesktopProjectPicker({
  projects,
  activeProjectId,
  isLoading,
  errorMessage,
  onSelect,
}: DesktopProjectPickerProps) {
  const [open, setOpen] = React.useState(false);
  const [search, setSearch] = React.useState("");
  const searchInputRef = React.useRef<HTMLInputElement | null>(null);

  React.useEffect(() => {
    if (!open) return;
    const timeout = window.setTimeout(() => searchInputRef.current?.focus(), 0);
    return () => window.clearTimeout(timeout);
  }, [open]);

  const filteredProjects = React.useMemo(() => {
    const query = search.trim().toLocaleLowerCase();
    if (!query) return projects;

    return projects.filter((project) => {
      const searchableText = `${project.name} ${project.workspace ?? ""} ${project.id}`;
      return searchableText.toLocaleLowerCase().includes(query);
    });
  }, [projects, search]);

  const handleOpenChange = (nextOpen: boolean) => {
    setOpen(nextOpen);
    if (!nextOpen) setSearch("");
  };

  const handleSelect = (projectId: string) => {
    onSelect(projectId);
    setOpen(false);
    setSearch("");
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
      >
        <div className="flex items-center justify-between border-b border-border/70 px-4 py-3">
          <div>
            <div className="text-sm font-semibold tracking-tight">Open project</div>
            <div className="mt-0.5 text-xs text-muted-foreground">
              Add a workspace to the title bar
            </div>
          </div>
          <span className="rounded-full bg-muted px-2 py-0.5 text-[11px] font-medium text-muted-foreground">
            {projects.length}
          </span>
        </div>

        <div className="p-2.5">
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

          <div className="mt-2 max-h-80 overflow-y-auto" role="listbox" aria-label="Projects">
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
        </div>
      </PopoverContent>
    </Popover>
  );
}
