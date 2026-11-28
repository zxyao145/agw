"use client";

import * as React from "react";
import { useQuery, useQueryClient } from "@agw/components/query";

import { apiGet, apiPost, apiPut, getApiErrorMessage } from "@agw/api";
import {
  Badge,
  Button,
  Checkbox,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  Empty,
  StaticTable,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@agw/components";

import {
  buildLocalSkillFormData,
  resolveLocalSkillStatus,
  type LocalSkill,
  type LocalSkillStatus,
  type LocalSkillsSource,
} from "../../../local-skills";

type LocalSkillsDialogProps = {
  source: LocalSkillsSource;
  open: boolean;
  onOpenChange(open: boolean): void;
};

type LocalSkillRow = LocalSkill & { status: LocalSkillStatus };

export function LocalSkillsDialog({ source, open, onOpenChange }: LocalSkillsDialogProps) {
  const queryClient = useQueryClient();
  const [selected, setSelected] = React.useState<Set<string> | null>(null);
  const [results, setResults] = React.useState<Record<string, string>>({});
  const [importing, setImporting] = React.useState(false);

  const scanQuery = useQuery({
    queryKey: ["local-skills", "scan"],
    queryFn: () => source.scan(),
    enabled: open,
    staleTime: 0,
  });
  const serverSkillsQuery = useQuery({
    queryKey: ["skills", "all"],
    queryFn: () => apiGet("/api/skills"),
    enabled: open,
    staleTime: 0,
  });

  const rows = React.useMemo<LocalSkillRow[]>(() => {
    if (!scanQuery.data) return [];
    const serverSkills = serverSkillsQuery.data ?? [];
    return scanQuery.data.skills.map((skill) => ({
      ...skill,
      status: resolveLocalSkillStatus(skill, serverSkills),
    }));
  }, [scanQuery.data, serverSkillsQuery.data]);
  const defaultSelection = React.useMemo(
    () => new Set(rows.filter((row) => row.status.kind === "New").map((row) => row.directoryName)),
    [rows],
  );
  const selectedDirectories = selected ?? defaultSelection;
  const selectedCount = rows.filter(
    (row) =>
      (row.status.kind === "New" || row.status.kind === "Changed") &&
      selectedDirectories.has(row.directoryName),
  ).length;
  const isRefreshing = scanQuery.isFetching || serverSkillsQuery.isFetching;
  const isBusy = importing || isRefreshing;
  const canImport =
    selectedCount > 0 &&
    !isBusy &&
    !scanQuery.isError &&
    !serverSkillsQuery.isError &&
    scanQuery.data?.exists === true &&
    serverSkillsQuery.data !== undefined;

  React.useEffect(() => {
    if (
      !open ||
      selected !== null ||
      isRefreshing ||
      scanQuery.isError ||
      serverSkillsQuery.isError ||
      !scanQuery.data ||
      !serverSkillsQuery.data
    )
      return;
    setSelected(defaultSelection);
  }, [
    defaultSelection,
    isRefreshing,
    open,
    scanQuery.data,
    scanQuery.isError,
    selected,
    serverSkillsQuery.data,
    serverSkillsQuery.isError,
  ]);

  const handleOpenChange = (nextOpen: boolean) => {
    if (importing) return;
    onOpenChange(nextOpen);
    if (!nextOpen) {
      setSelected(null);
      setResults({});
    }
  };

  const rescan = async () => {
    setResults({});
    await Promise.all([scanQuery.refetch(), serverSkillsQuery.refetch()]);
    setSelected(null);
  };

  const importSelected = async () => {
    const selectedRows = rows.filter(
      (row) =>
        (row.status.kind === "New" || row.status.kind === "Changed") &&
        selectedDirectories.has(row.directoryName),
    );
    if (selectedRows.length === 0 || !canImport) return;

    setImporting(true);
    setResults({});
    try {
      for (const row of selectedRows) {
        setResults((current) => ({ ...current, [row.directoryName]: "Importing…" }));
        try {
          const pkg = await source.pack(row.directoryName);
          if (row.status.kind === "New") {
            const body = buildLocalSkillFormData(row, row.status, pkg);
            await apiPost("/api/skills", { body });
            setResults((current) => ({ ...current, [row.directoryName]: "Imported" }));
          } else if (row.status.kind === "Changed") {
            const body = buildLocalSkillFormData(row, row.status, pkg);
            await apiPut("/api/skills/{id}", {
              params: { path: { id: row.status.skillId } },
              body,
            });
            setResults((current) => ({ ...current, [row.directoryName]: "Updated" }));
          } else {
            continue;
          }
        } catch (error) {
          setResults((current) => ({ ...current, [row.directoryName]: getApiErrorMessage(error) }));
          break;
        }
      }
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ["skills"] }),
        scanQuery.refetch(),
      ]);
      setSelected(null);
    } finally {
      setImporting(false);
    }
  };

  const toggleSelected = (directoryName: string, checked: boolean | "indeterminate") => {
    setSelected((current) => {
      const next = new Set(current ?? defaultSelection);
      if (checked === true) next.add(directoryName);
      else next.delete(directoryName);
      return next;
    });
  };

  const renderContent = () => {
    if (scanQuery.isLoading || serverSkillsQuery.isLoading) {
      return (
        <div className="py-8 text-center text-sm text-muted-foreground">Scanning local skills…</div>
      );
    }
    const refreshErrors = [
      scanQuery.isError
        ? `Failed to scan local skills: ${getApiErrorMessage(scanQuery.error)}`
        : null,
      serverSkillsQuery.isError
        ? `Failed to load server skills: ${getApiErrorMessage(serverSkillsQuery.error)}`
        : null,
    ].filter((message): message is string => message !== null);
    if (refreshErrors.length > 0 && (!scanQuery.data || !serverSkillsQuery.data)) {
      return (
        <div className="py-6 text-sm">
          {refreshErrors.map((message) => (
            <p className="text-destructive" key={message}>
              {message}
            </p>
          ))}
        </div>
      );
    }
    if (!scanQuery.data || !serverSkillsQuery.data) {
      return (
        <div className="py-8 text-center text-sm text-muted-foreground">Scanning local skills…</div>
      );
    }

    let content: React.ReactNode;
    if (!scanQuery.data.exists) {
      content = (
        <Empty>
          <div className="text-sm text-muted-foreground">
            {scanQuery.data.directory} doesn&apos;t exist.
          </div>
        </Empty>
      );
    } else if (rows.length === 0) {
      content = (
        <Empty>
          <div className="text-sm text-muted-foreground">
            No skills in {scanQuery.data.directory}.
          </div>
        </Empty>
      );
    } else {
      content = (
        <div className="min-h-0 flex-1 overflow-auto rounded-md border">
          <StaticTable embedded isEmpty={false}>
            <TableHeader>
              <TableRow>
                <TableHead className="w-10" />
                <TableHead>Name</TableHead>
                <TableHead>Description</TableHead>
                <TableHead>Files</TableHead>
                <TableHead>Status</TableHead>
                <TableHead>Result</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {rows.map((row) => {
                const canImport = row.status.kind === "New" || row.status.kind === "Changed";
                const checked = selectedDirectories.has(row.directoryName);
                return (
                  <TableRow key={row.directoryName}>
                    <TableCell>
                      <Checkbox
                        aria-label={`Select ${row.name ?? row.directoryName}`}
                        className="enabled:cursor-pointer"
                        checked={checked}
                        onCheckedChange={(value) => toggleSelected(row.directoryName, value)}
                        disabled={!canImport || isBusy}
                      />
                    </TableCell>
                    <TableCell className="min-w-36">
                      <div className="font-medium">{row.name ?? row.directoryName}</div>
                      {row.name !== null && row.name !== row.directoryName ? (
                        <div className="text-xs text-muted-foreground">{row.directoryName}</div>
                      ) : null}
                    </TableCell>
                    <TableCell className="max-w-sm">
                      <div className="line-clamp-2 text-sm text-muted-foreground">
                        {row.description}
                      </div>
                    </TableCell>
                    <TableCell className="whitespace-nowrap text-sm text-muted-foreground">
                      {row.fileCount} · {formatBytes(row.totalBytes)}
                    </TableCell>
                    <TableCell>
                      <Badge variant={statusVariant(row.status)}>{row.status.kind}</Badge>
                      {"reason" in row.status ? (
                        <div className="mt-1 max-w-56 text-xs text-muted-foreground">
                          {row.status.reason}
                        </div>
                      ) : null}
                    </TableCell>
                    <TableCell className="max-w-64 text-sm">
                      <span
                        className={
                          results[row.directoryName] &&
                          !["Imported", "Updated", "Importing…"].includes(
                            results[row.directoryName],
                          )
                            ? "text-destructive"
                            : "text-muted-foreground"
                        }
                      >
                        {results[row.directoryName]}
                      </span>
                    </TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </StaticTable>
        </div>
      );
    }

    return (
      <div className="flex min-h-0 flex-1 flex-col gap-3 overflow-hidden">
        {refreshErrors.map((message) => (
          <p className="text-sm text-destructive" key={message}>
            {message}
          </p>
        ))}
        {content}
      </div>
    );
  };

  return (
    <Dialog open={open} onOpenChange={handleOpenChange}>
      <DialogContent
        size="3xl"
        className="flex max-h-[90vh] flex-col overflow-hidden"
        showCloseButton={!importing}
      >
        <DialogHeader className="shrink-0">
          <DialogTitle>Local skills</DialogTitle>
          <DialogDescription className="break-all">
            Scan and import skills from {scanQuery.data?.directory ?? "~/.agents/skills"}.
          </DialogDescription>
        </DialogHeader>

        {renderContent()}

        <DialogFooter className="shrink-0">
          <Button variant="outline" onClick={() => void rescan()} disabled={isBusy}>
            Rescan
          </Button>
          <Button onClick={() => void importSelected()} disabled={!canImport}>
            Import ({selectedCount})
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function statusVariant(status: LocalSkillStatus): "secondary" | "outline" | "destructive" {
  if (status.kind === "New" || status.kind === "Synced") return "secondary";
  if (status.kind === "Invalid") return "destructive";
  return "outline";
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  const units = ["KB", "MB", "GB"];
  let size = bytes / 1024;
  let unit = 0;
  while (size >= 1024 && unit < units.length - 1) {
    size /= 1024;
    unit += 1;
  }
  return `${size.toFixed(1)} ${units[unit]}`;
}
