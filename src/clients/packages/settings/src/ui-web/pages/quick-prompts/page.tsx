"use client";

import * as React from "react";
import {
  ArrowDown,
  ArrowUp,
  GripVertical,
  Plus,
  RotateCcw,
  Save,
  Trash2,
  WandSparkles,
} from "lucide-react";

import {
  getQuickPromptManagement,
  saveQuickPrompts,
  type QuickPrompt,
  type QuickPromptDraft,
  type QuickPromptKind,
} from "@agw/api";
import {
  Badge,
  Button,
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  Empty,
  EmptyDescription,
  EmptyHeader,
  EmptyTitle,
  Input,
  Label,
  Separator,
  Skeleton,
  Tabs,
  TabsList,
  TabsTrigger,
  Textarea,
  cn,
  toast,
} from "@agw/components";

const blank = (): QuickPromptDraft => ({
  id: crypto.randomUUID(),
  label: "",
  description: "",
  text: "",
});

export default function QuickPromptsPage() {
  const [kind, setKind] = React.useState<QuickPromptKind>("user");
  const [system, setSystem] = React.useState<QuickPrompt[]>([]);
  const [user, setUser] = React.useState<QuickPrompt[]>([]);
  const [systemVersion, setSystemVersion] = React.useState<number | null>(null);
  const [userVersion, setUserVersion] = React.useState<number | null>(null);
  const [canManageSystem, setCanManageSystem] = React.useState(false);
  const [busy, setBusy] = React.useState(true);
  const [dirty, setDirty] = React.useState(false);
  const [draggingId, setDraggingId] = React.useState<string | null>(null);

  const load = React.useCallback(async () => {
    setBusy(true);
    try {
      const result = await getQuickPromptManagement();
      setSystem(result.system.items);
      setUser(result.user.items);
      setSystemVersion(result.systemVersion ?? null);
      setUserVersion(result.userVersion ?? null);
      setCanManageSystem(result.canManageSystem);
      setDirty(false);
    } catch {
      toast.error("Unable to load quick prompts");
    } finally {
      setBusy(false);
    }
  }, []);
  React.useEffect(() => {
    void load();
  }, [load]);

  const items = kind === "system" ? system : user;
  const setItems = (next: QuickPrompt[]) => {
    if (kind === "system") setSystem(next);
    else setUser(next);
    setDirty(true);
  };
  const editable = kind === "user" || canManageSystem;
  const update = (id: string, patch: Partial<QuickPromptDraft>) =>
    setItems(items.map((item) => (item.id === id ? { ...item, ...patch } : item)));
  const move = (index: number, delta: number) => {
    const target = index + delta;
    if (target < 0 || target >= items.length) return;
    const next = [...items];
    [next[index], next[target]] = [next[target], next[index]];
    setItems(next);
  };
  const drop = (id: string, targetIndex: number) => {
    const from = items.findIndex((item) => item.id === id);
    if (from < 0 || from === targetIndex) return;
    const next = [...items];
    const [moved] = next.splice(from, 1);
    next.splice(targetIndex, 0, moved);
    setItems(next);
  };
  const add = () => setItems([...items, { ...blank(), kind } as QuickPrompt]);
  const save = async () => {
    try {
      await saveQuickPrompts(
        kind,
        kind === "system" ? systemVersion : userVersion,
        items.map(({ kind: _kind, ...item }) => item),
      );
      await load();
      toast.success("Quick prompts saved");
    } catch {
      toast.error("Save failed. Reload if another user changed the list.");
    }
  };

  return (
    <div className="w-full max-w-4xl py-6">
      <header className="flex flex-wrap items-start justify-between gap-4">
        <div className="min-w-0">
          <p className="text-xs font-semibold uppercase tracking-[0.12em] text-primary">
            Personalization
          </p>
          <h1 className="mt-1 text-2xl font-semibold tracking-tight">Quick prompts</h1>
          <p className="mt-1 text-sm text-muted-foreground">
            Create reusable text inserts for the chat composer.
          </p>
        </div>
        {editable && (
          <Button onClick={add}>
            <Plus className="h-4 w-4" />
            Add prompt
          </Button>
        )}
      </header>

      <Card className="mt-6 gap-4">
        <CardHeader className="gap-3">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <Tabs value={kind} onValueChange={(value) => setKind(value as QuickPromptKind)}>
              <TabsList>
                <TabsTrigger value="user">My prompts</TabsTrigger>
                <TabsTrigger value="system" className="gap-2">
                  System prompts
                  {!canManageSystem && (
                    <Badge variant="outline" className="px-1.5 py-0 text-[10px] uppercase">
                      Read only
                    </Badge>
                  )}
                </TabsTrigger>
              </TabsList>
            </Tabs>
            <span className="text-xs tabular-nums text-muted-foreground">
              {items.length} prompt{items.length === 1 ? "" : "s"} · shown in this order
            </span>
          </div>
          <CardDescription className="flex items-center gap-2">
            <WandSparkles className="h-4 w-4 shrink-0" />
            {editable
              ? "Drag the handle or use the arrows to change the order."
              : "System prompts are managed by an administrator."}
          </CardDescription>
        </CardHeader>

        <CardContent>
          {busy ? (
            <div className="space-y-3">
              <Skeleton className="h-40 rounded-xl" />
              <Skeleton className="h-40 rounded-xl" />
            </div>
          ) : items.length === 0 ? (
            <Empty>
              <EmptyHeader>
                <WandSparkles className="h-5 w-5 text-muted-foreground" />
                <EmptyTitle>No prompts yet</EmptyTitle>
                <EmptyDescription>
                  {editable
                    ? "Add a prompt to reuse it from the chat composer."
                    : "Nothing has been shared here yet."}
                </EmptyDescription>
              </EmptyHeader>
            </Empty>
          ) : (
            <div className="space-y-3">
              {items.map((item, index) => (
                <div
                  key={item.id}
                  onDragOver={(event) => {
                    if (draggingId && draggingId !== item.id) event.preventDefault();
                  }}
                  onDrop={(event) => {
                    event.preventDefault();
                    if (draggingId) drop(draggingId, index);
                    setDraggingId(null);
                  }}
                  className={cn(
                    "rounded-xl border bg-card/40 p-4 transition-colors",
                    draggingId && draggingId !== item.id && "border-ring/60 bg-accent/40",
                    draggingId === item.id && "opacity-60",
                  )}
                >
                  <div className="flex items-center gap-2">
                    <span
                      draggable={editable}
                      onDragStart={(event) => {
                        event.dataTransfer.effectAllowed = "move";
                        setDraggingId(item.id);
                      }}
                      onDragEnd={() => setDraggingId(null)}
                      title={editable ? "Drag to reorder" : undefined}
                      className={cn(
                        "flex size-5 shrink-0 items-center justify-center rounded text-muted-foreground/50",
                        editable ? "cursor-grab hover:text-muted-foreground" : "opacity-40",
                      )}
                    >
                      <GripVertical className="h-4 w-4" />
                    </span>
                    <span className="flex size-5 shrink-0 items-center justify-center rounded-full bg-muted text-[11px] font-medium tabular-nums text-muted-foreground">
                      {index + 1}
                    </span>
                    <Badge
                      variant="outline"
                      className="h-5 px-1.5 text-[10px] font-semibold uppercase tracking-wide text-muted-foreground"
                    >
                      {item.kind}
                    </Badge>
                    <div className="ml-auto flex items-center">
                      <Button
                        size="icon-sm"
                        variant="ghost"
                        className="text-muted-foreground"
                        disabled={!editable || index === 0}
                        onClick={() => move(index, -1)}
                        aria-label="Move prompt up"
                      >
                        <ArrowUp className="h-4 w-4" />
                      </Button>
                      <Button
                        size="icon-sm"
                        variant="ghost"
                        className="text-muted-foreground"
                        disabled={!editable || index === items.length - 1}
                        onClick={() => move(index, 1)}
                        aria-label="Move prompt down"
                      >
                        <ArrowDown className="h-4 w-4" />
                      </Button>
                      {editable && (
                        <>
                          <Separator orientation="vertical" className="mx-1 h-5" />
                          <Button
                            size="icon-sm"
                            variant="ghost"
                            onClick={() => setItems(items.filter((x) => x.id !== item.id))}
                            aria-label="Delete prompt"
                          >
                            <Trash2 className="h-4 w-4 text-destructive" />
                          </Button>
                        </>
                      )}
                    </div>
                  </div>

                  <div className="mt-3 grid gap-3 sm:grid-cols-2">
                    <div className="space-y-1.5">
                      <Label htmlFor={`prompt-name-${item.id}`}>Name</Label>
                      <Input
                        id={`prompt-name-${item.id}`}
                        disabled={!editable}
                        placeholder="Implement"
                        value={item.label}
                        onChange={(event) => update(item.id, { label: event.target.value })}
                      />
                    </div>
                    <div className="space-y-1.5">
                      <Label htmlFor={`prompt-description-${item.id}`}>Description</Label>
                      <Input
                        id={`prompt-description-${item.id}`}
                        disabled={!editable}
                        placeholder="On the current branch"
                        value={item.description ?? ""}
                        onChange={(event) => update(item.id, { description: event.target.value })}
                      />
                    </div>
                  </div>

                  <div className="mt-3 space-y-1.5">
                    <Label htmlFor={`prompt-text-${item.id}`}>Prompt text</Label>
                    <Textarea
                      id={`prompt-text-${item.id}`}
                      disabled={!editable}
                      className="resize-y"
                      rows={3}
                      placeholder="Execute on the current branch."
                      value={item.text}
                      onChange={(event) => update(item.id, { text: event.target.value })}
                    />
                  </div>
                </div>
              ))}
            </div>
          )}
        </CardContent>
      </Card>

      {editable && (
        <div className="sticky bottom-4 z-10 mt-4 flex flex-wrap items-center justify-between gap-3 rounded-xl border bg-background/85 px-4 py-3 shadow-sm backdrop-blur">
          <span className="flex items-center gap-2 text-sm text-muted-foreground">
            <span
              className={cn("size-1.5 rounded-full", dirty ? "bg-amber-500" : "bg-emerald-500/70")}
            />
            {dirty ? "Unsaved changes" : "All changes saved"}
          </span>
          <div className="flex items-center gap-2">
            <Button variant="ghost" disabled={!dirty || busy} onClick={() => void load()}>
              <RotateCcw className="h-4 w-4" />
              Discard
            </Button>
            <Button disabled={!dirty || busy} onClick={save}>
              <Save className="h-4 w-4" />
              Save
            </Button>
          </div>
        </div>
      )}
    </div>
  );
}
