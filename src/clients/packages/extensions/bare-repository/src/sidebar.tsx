import {
  Select,
  SelectContent,
  SelectGroup,
  SelectItem,
  SelectLabel,
  SelectTrigger,
  SelectValue,
} from "./select";
import type {
  ContextProject,
  ExtensionApi,
  RepositoryChange,
  RepositoryTreeEntry,
  RepositoryWorkspaceState,
  WorkspaceRepository,
} from "@agw/extension-sdk";
import {
  ChevronDown,
  ChevronRight,
  Download,
  Eye,
  File,
  Folder,
  FolderOpen,
  GitMerge,
  Plus,
  RefreshCw,
  RotateCcw,
  Settings2,
  Undo2,
} from "lucide-react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { ManageParams } from "./project-workspace";
import { readProxyUrl } from "./proxy-settings";
import { errorMessage, startView, type FileParams, type OpenRequest } from "./view";
import "./base.css";
import "./sidebar.css";

const CHANGES_POLL_MS = 5000;

const STATUS_LETTERS: Record<RepositoryChange["status"], string> = {
  modified: "M",
  added: "A",
  deleted: "D",
  renamed: "R",
  untracked: "U",
  conflicted: "!",
};

type Checkout = { branch: string; location: string; feature: string | null };

function cloudUri(serverId: string, projectId: string, path: string): string {
  const encoded = path.split("/").map(encodeURIComponent).join("/");
  return `agw-cloud://${serverId}/${projectId}/workspace/${encoded}`;
}

/** 仓库的主工作副本与 feature worktree 各自检出的分支。The branches checked out by a repository's root and feature worktrees. */
function checkoutsOf(state: RepositoryWorkspaceState, repo: WorkspaceRepository): Checkout[] {
  const checkouts: Checkout[] = repo.branch
    ? [{ branch: repo.branch, location: repo.name, feature: null }]
    : [];
  for (const feature of state.features)
    for (const entry of feature.repositories)
      if (entry.name === repo.name && entry.branch && !entry.error)
        checkouts.push({
          branch: entry.branch,
          location: `.worktrees/${feature.name}/${repo.name}`,
          feature: feature.name,
        });
  return checkouts;
}

function selectionKey(projectId: string) {
  return `bare-repository:${projectId}:selection`;
}

function RepositorySidebar({
  api,
  serverId,
  project,
  request,
}: {
  api: ExtensionApi;
  serverId: string;
  project: ContextProject;
  request: OpenRequest;
}) {
  const [state, setState] = useState<RepositoryWorkspaceState | null>(null);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [selection, setSelection] = useState<{ repository: string; branch: string } | null>(() => {
    try {
      return JSON.parse(localStorage.getItem(selectionKey(project.id)) ?? "null");
    } catch {
      return null;
    }
  });
  const [tab, setTab] = useState<"files" | "changes">("files");
  const [tree, setTree] = useState<ReadonlyMap<string, RepositoryTreeEntry[]>>(new Map());
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(new Set());
  const [checkout, setCheckout] = useState<string | null>(null);
  const [treeError, setTreeError] = useState("");
  const [changes, setChanges] = useState<RepositoryChange[]>([]);
  const [selectedPath, setSelectedPath] = useState("");
  const generation = useRef(0);

  const loadState = useCallback(async () => {
    try {
      const value = await api.repositories.get(project.id);
      setState(value);
      setError("");
      return value;
    } catch (caught) {
      setError(errorMessage(caught));
      return null;
    }
  }, [api, project.id]);

  // 侧边栏每次显示时重新读取。Reload whenever the sidebar is shown.
  useEffect(() => {
    void loadState();
  }, [loadState, request]);

  const repositories = state?.repositories.filter((repo) => !repo.error) ?? [];
  const repo =
    repositories.find((item) => item.name === selection?.repository) ?? repositories.at(0);
  const branch =
    repo && selection?.repository === repo.name && selection.branch
      ? selection.branch
      : (repo?.branch ?? repo?.defaultBranch ?? "");
  const checkouts = useMemo(() => (state && repo ? checkoutsOf(state, repo) : []), [state, repo]);
  const current = checkouts.find((item) => item.branch === branch && item.location === checkout);

  function select(repository: string, nextBranch: string) {
    const value = { repository, branch: nextBranch };
    setSelection(value);
    try {
      localStorage.setItem(selectionKey(project.id), JSON.stringify(value));
    } catch {
      // 存储不可用时只保留在内存中。Keep the selection in memory when storage is unavailable.
    }
  }

  const loadDirectory = useCallback(
    async (path: string, gen: number) => {
      if (!repo || !branch) return;
      try {
        const value = await api.repositories.tree({
          projectId: project.id,
          repository: repo.name,
          branch,
          path,
        });
        if (gen !== generation.current) return;
        setTree((previous) => new Map(previous).set(path, value.entries));
        if (path === "") {
          setCheckout(value.checkout);
          setTreeError("");
        }
        return value;
      } catch (caught) {
        if (gen !== generation.current) return;
        if (path === "") setTreeError(errorMessage(caught));
        else
          setExpanded((previous) => {
            const next = new Set(previous);
            next.delete(path);
            return next;
          });
      }
    },
    [api, project.id, repo?.name, branch],
  );

  const loadChanges = useCallback(async () => {
    if (!repo || !branch) return;
    const gen = generation.current;
    try {
      const value = await api.repositories.changes({
        projectId: project.id,
        repository: repo.name,
        branch,
      });
      if (gen === generation.current) setChanges(value);
    } catch {
      if (gen === generation.current) setChanges([]);
    }
  }, [api, project.id, repo?.name, branch]);

  // 切换仓库或分支时重置文件树。Reset the tree when the repository or branch changes.
  useEffect(() => {
    const gen = ++generation.current;
    setTree(new Map());
    setExpanded(new Set());
    setCheckout(null);
    setChanges([]);
    setSelectedPath("");
    void loadDirectory("", gen);
  }, [loadDirectory]);

  const refresh = useCallback(async () => {
    const gen = generation.current;
    await loadState();
    await Promise.all(["", ...expanded].map((path) => loadDirectory(path, gen)));
    await loadChanges();
  }, [loadState, expanded, loadDirectory, loadChanges]);

  useEffect(() => {
    void loadChanges();
    const timer = setInterval(() => {
      if (document.visibilityState === "visible") void refresh();
    }, CHANGES_POLL_MS);
    return () => clearInterval(timer);
  }, [refresh, loadChanges]);

  function toggle(path: string) {
    if (expanded.has(path)) {
      setExpanded((previous) => {
        const next = new Set(previous);
        next.delete(path);
        return next;
      });
      return;
    }
    setExpanded((previous) => new Set(previous).add(path));
    if (!tree.has(path)) void loadDirectory(path, generation.current);
  }

  async function open(path: string) {
    if (!repo) return;
    setSelectedPath(path);
    try {
      const root = await loadDirectory("", generation.current);
      if (!root) return;
      if (root.checkout)
        await api.workspace.openFile(cloudUri(serverId, project.id, `${root.checkout}/${path}`));
      else
        await api.workspace.openMainView("file", {
          repository: repo.name,
          branch,
          path,
        } satisfies FileParams);
    } catch (caught) {
      if ((caught as { code?: string }).code !== "ERR_CANCELED") setError(errorMessage(caught));
    }
  }

  function manage(params: ManageParams = {}) {
    void api.workspace.openMainView("repositories", params);
  }

  async function operate(operation: () => Promise<RepositoryWorkspaceState>) {
    setBusy(true);
    setError("");
    try {
      setState(await operation());
      await refresh();
    } catch (caught) {
      setError(errorMessage(caught));
    } finally {
      setBusy(false);
    }
  }

  async function merge(feature: string) {
    if (!repo) return;
    const decision = await api.dialog.showMessageBox({
      type: "warning",
      message: `Merge ${branch} into ${repo.name}/${repo.defaultBranch}?`,
      detail:
        "Creates a merge commit in the root checkout. If the merge conflicts it is aborted and nothing changes.",
      buttons: ["Cancel", "Merge"],
      defaultId: 0,
      cancelId: 0,
    });
    if (decision === 1)
      await operate(() =>
        api.repositories.merge({ projectId: project.id, feature, repository: repo.name }),
      );
  }

  if (!state)
    return (
      <main className="sidebar">
        {error ? <p className="error">{error}</p> : <p className="hint">Loading repositories…</p>}
      </main>
    );

  const worktree = current?.feature
    ? state.features
        .find((item) => item.name === current.feature)
        ?.repositories.find((entry) => entry.name === repo?.name)
    : undefined;
  const checkedOut = new Set(checkouts.map((item) => item.branch));
  const offDefault = repo ? repo.branch !== repo.defaultBranch : false;

  // 当前位置的操作：worktree 合并，主工作副本同步或恢复默认分支。The action for the current location: merge a worktree,
  // or sync or restore the root checkout.
  const contextAction =
    !repo || !checkout ? null : current?.feature ? (
      <button
        className="icon"
        aria-label={`Merge into ${repo.defaultBranch}`}
        disabled={
          busy || !worktree || worktree.dirty || worktree.ahead === 0 || offDefault || repo.dirty
        }
        title={
          worktree?.dirty
            ? "Commit the worktree changes first."
            : offDefault
              ? `Restore ${repo.name} to ${repo.defaultBranch} first.`
              : repo.dirty
                ? `The root checkout of ${repo.name} has local changes.`
                : worktree?.ahead === 0
                  ? `No commits ahead of ${repo.defaultBranch}.`
                  : `Merge into ${repo.defaultBranch}`
        }
        onClick={() => void merge(current.feature!)}
      >
        <GitMerge size={15} />
      </button>
    ) : offDefault ? (
      <button
        className="icon"
        aria-label={`Restore ${repo.defaultBranch}`}
        title={repo.dirty ? "Commit or stash the changes first." : `Restore ${repo.defaultBranch}`}
        disabled={busy || repo.dirty}
        onClick={() =>
          void operate(() =>
            api.repositories.restoreDefaultBranch({ projectId: project.id, repository: repo.name }),
          )
        }
      >
        <Undo2 size={15} />
      </button>
    ) : (
      <button
        className="icon"
        aria-label="Sync"
        disabled={busy || repo.ahead === null}
        title={
          repo.ahead === null
            ? "The default branch has no upstream."
            : `Sync with origin/${repo.defaultBranch}${repo.behind ? ` (${repo.behind} behind)` : ""}`
        }
        onClick={() =>
          void operate(async () =>
            api.repositories.sync({
              projectId: project.id,
              repository: repo.name,
              proxyUrl: await readProxyUrl(api),
            }),
          )
        }
      >
        <RefreshCw size={15} />
        {repo.behind ? <span className="icon-count">{repo.behind}</span> : null}
      </button>
    );

  return (
    <main className="sidebar" aria-busy={busy}>
      <header className="sidebar-head">
        <h1>Repositories</h1>
        <div className="actions">
          {contextAction}
          <button
            className="icon"
            title="New feature"
            aria-label="New feature"
            disabled={!repo}
            onClick={() => manage({ action: "feature" })}
          >
            <Plus size={15} />
          </button>
          <button
            className="icon"
            title="Clone repository"
            aria-label="Clone repository"
            onClick={() => manage({ action: "clone" })}
          >
            <Download size={15} />
          </button>
          <button
            className="icon"
            title="Refresh"
            aria-label="Refresh"
            onClick={() => void refresh()}
          >
            <RotateCcw size={15} />
          </button>
          <button
            className="icon"
            title="Manage repositories"
            aria-label="Manage repositories"
            onClick={() => manage()}
          >
            <Settings2 size={15} />
          </button>
        </div>
      </header>
      {error && (
        <p className="error inset" role="alert">
          {error}
        </p>
      )}
      {!repo ? (
        <div className="sidebar-empty">
          <p>No repositories at the Project root.</p>
          <button className="primary" onClick={() => manage({ action: "clone" })}>
            <Download size={15} /> Clone a repository
          </button>
        </div>
      ) : (
        <>
          <div className="pickers">
            <Select
              value={repo.name}
              onValueChange={(name) => {
                const next = repositories.find((item) => item.name === name)!;
                select(next.name, next.branch || next.defaultBranch);
              }}
            >
              <SelectTrigger aria-label="Repository">
                <SelectValue />
              </SelectTrigger>
              <SelectContent position="popper" side="bottom" align="start" avoidCollisions={false}>
                {repositories.map((item) => (
                  <SelectItem key={item.name} value={item.name}>
                    {item.name}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            <Select value={branch} onValueChange={(value) => select(repo.name, value)}>
              <SelectTrigger aria-label="Branch">
                <SelectValue />
              </SelectTrigger>
              <SelectContent position="popper" side="bottom" align="start" avoidCollisions={false}>
                <SelectGroup>
                  <SelectLabel>Checked out</SelectLabel>
                  {checkouts.map((item) => (
                    <SelectItem key={item.location} value={item.branch}>
                      {item.branch} — {item.feature ?? "main checkout"}
                    </SelectItem>
                  ))}
                </SelectGroup>
                <SelectGroup>
                  <SelectLabel>Local branches</SelectLabel>
                  {repo.branches
                    .filter((item) => !checkedOut.has(item))
                    .map((item) => (
                      <SelectItem key={item} value={item}>
                        {item}
                      </SelectItem>
                    ))}
                </SelectGroup>
                {repo.remoteBranches.length > 0 && (
                  <SelectGroup>
                    <SelectLabel>Remote branches</SelectLabel>
                    {repo.remoteBranches.map((item) => (
                      <SelectItem key={item} value={item}>
                        {item}
                      </SelectItem>
                    ))}
                  </SelectGroup>
                )}
              </SelectContent>
            </Select>
          </div>
          {!checkout && (
            <div className="location">
              <Eye size={14} />
              <span>Read-only · not checked out</span>
            </div>
          )}
          <div className="tabs" role="tablist">
            <button role="tab" aria-selected={tab === "files"} onClick={() => setTab("files")}>
              Files
            </button>
            <button
              role="tab"
              aria-selected={tab === "changes"}
              disabled={!checkout}
              title={checkout ? undefined : "Only checked-out branches have uncommitted changes."}
              onClick={() => setTab("changes")}
            >
              Changes
              {checkout && changes.length > 0 ? (
                <span className="count">{changes.length}</span>
              ) : null}
            </button>
          </div>
          {tab === "changes" && checkout ? (
            <ul className="changes" aria-label="Uncommitted changes">
              {changes.length === 0 && <li className="hint">No uncommitted changes.</li>}
              {changes.map((change) => (
                <li key={change.path}>
                  <button
                    className="tree-item"
                    aria-current={selectedPath === change.path ? "true" : undefined}
                    disabled={change.status === "deleted"}
                    title={change.status === "deleted" ? `${change.path} (deleted)` : change.path}
                    onClick={() => void open(change.path)}
                  >
                    <span className={`status ${change.status}`}>
                      {STATUS_LETTERS[change.status]}
                    </span>
                    <span className="name">{change.path.split("/").at(-1)}</span>
                    <span className="dir">{change.path.split("/").slice(0, -1).join("/")}</span>
                  </button>
                </li>
              ))}
            </ul>
          ) : (
            <div className="tree" role="tree" aria-label="Files">
              {treeError ? (
                <p className="error inset">{treeError}</p>
              ) : !tree.has("") ? (
                <p className="hint">Loading…</p>
              ) : (
                <TreeLevel
                  path=""
                  depth={0}
                  tree={tree}
                  expanded={expanded}
                  selectedPath={selectedPath}
                  changes={checkout ? changes : []}
                  onToggle={toggle}
                  onOpen={(path) => void open(path)}
                />
              )}
            </div>
          )}
        </>
      )}
    </main>
  );
}

function TreeLevel({
  path,
  depth,
  tree,
  expanded,
  selectedPath,
  changes,
  onToggle,
  onOpen,
}: {
  path: string;
  depth: number;
  tree: ReadonlyMap<string, RepositoryTreeEntry[]>;
  expanded: ReadonlySet<string>;
  selectedPath: string;
  changes: RepositoryChange[];
  onToggle(path: string): void;
  onOpen(path: string): void;
}) {
  const entries = tree.get(path);
  if (!entries)
    return (
      <p className="hint" style={{ paddingLeft: 8 + depth * 12 }}>
        Loading…
      </p>
    );
  if (entries.length === 0 && depth === 0) return <p className="hint">Empty repository.</p>;
  return (
    <>
      {entries.map((entry) => {
        const open = expanded.has(entry.path);
        const status = changes.find((change) =>
          entry.isDirectory ? change.path.startsWith(`${entry.path}/`) : change.path === entry.path,
        )?.status;
        return (
          <div
            key={entry.path}
            role="treeitem"
            aria-expanded={entry.isDirectory ? open : undefined}
          >
            <button
              className="tree-item"
              style={{ paddingLeft: 6 + depth * 12 }}
              aria-current={selectedPath === entry.path ? "true" : undefined}
              data-path={entry.path}
              title={entry.path}
              onClick={() => (entry.isDirectory ? onToggle(entry.path) : onOpen(entry.path))}
            >
              {entry.isDirectory ? (
                open ? (
                  <ChevronDown size={14} />
                ) : (
                  <ChevronRight size={14} />
                )
              ) : (
                <span className="chevron-space" />
              )}
              {entry.isDirectory ? (
                open ? (
                  <FolderOpen size={14} />
                ) : (
                  <Folder size={14} />
                )
              ) : (
                <File size={14} />
              )}
              <span className={`name${status ? ` ${status}` : ""}`}>{entry.name}</span>
              {status && !entry.isDirectory && (
                <span className={`status ${status}`}>{STATUS_LETTERS[status]}</span>
              )}
              {status && entry.isDirectory && <i className={`dot ${status}`} />}
            </button>
            {entry.isDirectory && open && (
              <TreeLevel
                path={entry.path}
                depth={depth + 1}
                tree={tree}
                expanded={expanded}
                selectedPath={selectedPath}
                changes={changes}
                onToggle={onToggle}
                onOpen={onOpen}
              />
            )}
          </div>
        );
      })}
    </>
  );
}

startView(({ api, context, project, request }) => (
  <RepositorySidebar api={api} serverId={context.server.id} project={project} request={request} />
));
