import type {
  ContextProject,
  ExtensionApi,
  FeatureRepository,
  RepositoryWorkspaceState,
  WorkspaceFeature,
  WorkspaceRepository,
} from "@agw/extension-sdk";
import {
  AlertTriangle,
  ArrowDown,
  ArrowUp,
  Download,
  FolderGit2,
  FolderOpen,
  GitBranch,
  GitMerge,
  Plus,
  RefreshCw,
  RotateCcw,
  Undo2,
} from "lucide-react";
import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { CloneDialog, FeatureDialog, SwitchBranchDialog, isoDate } from "./dialogs";
import type { OpenRequest } from "./view";

/**
 * 侧边栏打开管理视图时的参数：打开弹窗或选中 feature。Params when the sidebar opens the management view: open a dialog
 * or select a feature.
 */
export type ManageParams = { action?: "clone" | "feature"; feature?: string };

type Selection = { kind: "main" } | { kind: "feature"; name: string };

type Dialog =
  | { kind: "clone" }
  | { kind: "feature"; feature?: string }
  | { kind: "switch"; feature: string; repo: FeatureRepository };

const DIALOG = "dialog";

function message(caught: unknown): string {
  return caught instanceof Error ? caught.message : String(caught);
}

function without(errors: Readonly<Record<string, string>>, key: string) {
  const next = { ...errors };
  delete next[key];
  return next;
}

function offDefault(repo: WorkspaceRepository): boolean {
  return !repo.error && repo.branch !== repo.defaultBranch;
}

function needsAttention(repo: WorkspaceRepository | FeatureRepository): boolean {
  return Boolean(repo.error) || ("defaultBranch" in repo && offDefault(repo));
}

export function ProjectWorkspace({
  api,
  project,
  request,
}: {
  api: ExtensionApi;
  project: ContextProject;
  request: OpenRequest;
}) {
  const [state, setState] = useState<RepositoryWorkspaceState | null>(null);
  const [loadError, setLoadError] = useState("");
  const [pending, setPending] = useState<ReadonlySet<string>>(new Set());
  const [errors, setErrors] = useState<Readonly<Record<string, string>>>({});
  const [notice, setNotice] = useState("");
  const [selection, setSelection] = useState<Selection>({ kind: "main" });
  const [dialog, setDialog] = useState<Dialog | null>(null);
  const active = useRef(true);

  const refresh = useCallback(async () => {
    setLoadError("");
    try {
      const value = await api.repositories.get(project.id);
      if (active.current) setState(value);
    } catch (caught) {
      if (active.current) setLoadError(message(caught));
    }
  }, [api, project.id]);

  useEffect(() => {
    active.current = true;
    void refresh();
    return () => {
      active.current = false;
    };
  }, [refresh]);

  useEffect(() => {
    const params = (request.params ?? {}) as ManageParams;
    if (params.feature) setSelection({ kind: "feature", name: params.feature });
    if (params.action === "clone") setDialog({ kind: "clone" });
    if (params.action === "feature") setDialog({ kind: "feature", feature: params.feature });
  }, [request]);

  async function run(
    key: string,
    operation: () => Promise<RepositoryWorkspaceState>,
    done = "",
  ): Promise<RepositoryWorkspaceState | null> {
    setPending((previous) => new Set(previous).add(key));
    setErrors((previous) => without(previous, key));
    setNotice("");
    try {
      const value = await operation();
      if (active.current) {
        setState(value);
        setNotice(done);
      }
      return value;
    } catch (caught) {
      if (active.current) setErrors((previous) => ({ ...previous, [key]: message(caught) }));
      return null;
    } finally {
      if (active.current)
        setPending((previous) => {
          const next = new Set(previous);
          next.delete(key);
          return next;
        });
    }
  }

  function closeDialog() {
    setDialog(null);
    setErrors((previous) => without(previous, DIALOG));
  }

  if (!state)
    return (
      <main aria-busy={!loadError} className="centered">
        {loadError ? (
          <div className="empty">
            <AlertTriangle size={28} />
            <h1>Cannot read the repositories</h1>
            <p className="error">{loadError}</p>
            <button onClick={() => void refresh()}>Try again</button>
          </div>
        ) : (
          <p>Loading repositories…</p>
        )}
      </main>
    );

  const repositories = state.repositories;
  const feature =
    selection.kind === "feature"
      ? state.features.find((item) => item.name === selection.name)
      : undefined;
  const mainDefaults = new Map(repositories.map((repo) => [repo.name, repo]));
  const busy = pending.size > 0;

  async function merge(item: WorkspaceFeature, repo: FeatureRepository) {
    const main = mainDefaults.get(repo.name);
    const decision = await api.dialog.showMessageBox({
      type: "warning",
      message: `Merge ${repo.branch} into ${repo.name}/${main?.defaultBranch}?`,
      detail:
        "Creates a merge commit in the root checkout. If the merge conflicts it is aborted and nothing changes.",
      buttons: ["Cancel", "Merge"],
      defaultId: 0,
      cancelId: 0,
    });
    if (decision === 1)
      await run(
        `${item.name}/${repo.name}`,
        () =>
          api.repositories.merge({
            projectId: project.id,
            feature: item.name,
            repository: repo.name,
          }),
        `Merged ${repo.branch} into ${repo.name}/${main?.defaultBranch}.`,
      );
  }

  async function syncAll() {
    for (const repo of repositories.filter((item) => !item.error && !offDefault(item)))
      await run(`main/${repo.name}`, () =>
        api.repositories.sync({ projectId: project.id, repository: repo.name }),
      );
    if (active.current) setNotice("Default branches synced.");
  }

  const content =
    repositories.length === 0 && state.features.length === 0 ? (
      <Welcome onClone={() => setDialog({ kind: "clone" })} />
    ) : feature ? (
      <section className="panel" aria-label={feature.name}>
        <div className="panel-head">
          <div>
            <h2>{feature.name}</h2>
            <p>
              <code>.worktrees/{feature.name}</code> · branch <code>{feature.name}</code>
            </p>
          </div>
          <button onClick={() => setDialog({ kind: "feature", feature: feature.name })}>
            <Plus size={16} /> Add repositories
          </button>
        </div>
        {feature.repositories.length === 0 && (
          <p className="hint">This feature has no worktrees.</p>
        )}
        <ul className="rows">
          {feature.repositories.map((repo) => {
            const key = `${feature.name}/${repo.name}`;
            const main = mainDefaults.get(repo.name);
            const mergeBlock = repo.dirty
              ? "Commit the worktree changes first."
              : !main || main.error
                ? "The root checkout cannot be read."
                : offDefault(main)
                  ? `Restore ${repo.name} to ${main.defaultBranch} first.`
                  : main.dirty
                    ? `The root checkout of ${repo.name} has local changes.`
                    : repo.ahead === 0
                      ? `No commits ahead of ${main.defaultBranch}.`
                      : "";
            return (
              <Row
                key={key}
                name={repo.name}
                directory={`.worktrees/${feature.name}/${repo.name}`}
                busy={pending.has(key)}
                error={errors[key] ?? repo.error ?? undefined}
                status={
                  repo.error ? null : (
                    <>
                      <Badge icon={<GitBranch size={13} />}>{repo.branch || "detached HEAD"}</Badge>
                      {main && !main.error && (
                        <Badge title={`Compared with ${main.defaultBranch}`}>
                          <ArrowUp size={12} />
                          {repo.ahead} <ArrowDown size={12} />
                          {repo.behind} vs {main.defaultBranch}
                        </Badge>
                      )}
                      {repo.dirty && <Badge tone="warning">Uncommitted changes</Badge>}
                    </>
                  )
                }
                actions={
                  repo.error ? null : (
                    <>
                      <button
                        disabled={repo.dirty}
                        title={repo.dirty ? "Commit the worktree changes first." : undefined}
                        onClick={() => setDialog({ kind: "switch", feature: feature.name, repo })}
                      >
                        <GitBranch size={15} /> Switch
                      </button>
                      <button
                        className="primary"
                        disabled={Boolean(mergeBlock)}
                        title={mergeBlock || `Merge into ${main?.defaultBranch}`}
                        onClick={() => void merge(feature, repo)}
                      >
                        <GitMerge size={15} /> Merge
                      </button>
                    </>
                  )
                }
              />
            );
          })}
        </ul>
      </section>
    ) : (
      <section className="panel" aria-label="Main checkouts">
        <div className="panel-head">
          <div>
            <h2>Main checkouts</h2>
            <p>
              Each repository at the Project root stays on its default branch. Develop in feature
              worktrees and merge back.
            </p>
          </div>
          <div className="actions">
            <button onClick={() => setDialog({ kind: "clone" })}>
              <Download size={16} /> Clone
            </button>
            <button disabled={busy || repositories.length === 0} onClick={() => void syncAll()}>
              <RefreshCw size={16} /> Sync all
            </button>
          </div>
        </div>
        {repositories.length === 0 && <p className="hint">No repositories at the Project root.</p>}
        <ul className="rows">
          {repositories.map((repo) => {
            const key = `main/${repo.name}`;
            const off = offDefault(repo);
            return (
              <Row
                key={key}
                name={repo.name}
                directory={repo.name}
                busy={pending.has(key)}
                error={errors[key] ?? repo.error ?? undefined}
                status={
                  repo.error ? null : (
                    <>
                      <Badge icon={<GitBranch size={13} />} tone={off ? "warning" : undefined}>
                        {off
                          ? `${repo.branch || "detached HEAD"} (default: ${repo.defaultBranch})`
                          : repo.defaultBranch}
                      </Badge>
                      {repo.ahead === null ? (
                        <Badge tone="muted">No upstream</Badge>
                      ) : (
                        (repo.ahead > 0 || repo.behind! > 0) && (
                          <Badge title="Compared with the upstream">
                            <ArrowUp size={12} />
                            {repo.ahead} <ArrowDown size={12} />
                            {repo.behind}
                          </Badge>
                        )
                      )}
                      {repo.dirty && <Badge tone="warning">Uncommitted changes</Badge>}
                    </>
                  )
                }
                actions={
                  repo.error ? null : off ? (
                    <button
                      disabled={repo.dirty}
                      title={repo.dirty ? "Commit or stash the changes first." : undefined}
                      onClick={() =>
                        void run(
                          key,
                          () =>
                            api.repositories.restoreDefaultBranch({
                              projectId: project.id,
                              repository: repo.name,
                            }),
                          `${repo.name} is back on ${repo.defaultBranch}.`,
                        )
                      }
                    >
                      <Undo2 size={15} /> Restore {repo.defaultBranch}
                    </button>
                  ) : (
                    <button
                      disabled={repo.ahead === null}
                      title={
                        repo.ahead === null ? "The default branch has no upstream." : undefined
                      }
                      onClick={() =>
                        void run(
                          key,
                          () =>
                            api.repositories.sync({ projectId: project.id, repository: repo.name }),
                          `${repo.name} synced with origin/${repo.defaultBranch}.`,
                        )
                      }
                    >
                      <RefreshCw size={15} /> Sync
                    </button>
                  )
                }
              />
            );
          })}
        </ul>
      </section>
    );

  return (
    <main aria-busy={busy}>
      <header className="top">
        <div>
          <h1>Bare Repository</h1>
          <p>{project.name}</p>
        </div>
        <div className="actions">
          <span role="status" className="notice">
            {notice}
          </span>
          <button
            className="icon"
            aria-label="Refresh"
            title="Refresh"
            onClick={() => void refresh()}
          >
            <RotateCcw size={16} />
          </button>
        </div>
      </header>
      {loadError && (
        <p className="error banner" role="alert">
          {loadError}
        </p>
      )}
      <div className="layout">
        <nav aria-label="Workspace">
          <button
            className="nav-item"
            aria-current={!feature ? "page" : undefined}
            onClick={() => setSelection({ kind: "main" })}
          >
            <FolderGit2 size={16} />
            <span>Main checkouts</span>
            {repositories.some(needsAttention) && <AlertTriangle size={14} className="warn" />}
            <small>{repositories.length}</small>
          </button>
          <div className="nav-head">
            <span>Features</span>
            <button
              className="icon"
              aria-label="New feature"
              title="New feature"
              disabled={repositories.length === 0}
              onClick={() => setDialog({ kind: "feature" })}
            >
              <Plus size={15} />
            </button>
          </div>
          {state.features.length === 0 && <p className="hint">No features yet.</p>}
          {state.features.map((item) => (
            <button
              key={item.name}
              className="nav-item"
              data-feature={item.name}
              aria-current={feature?.name === item.name ? "page" : undefined}
              onClick={() => setSelection({ kind: "feature", name: item.name })}
            >
              <GitBranch size={16} />
              <span>{item.name}</span>
              {item.repositories.some(needsAttention) && (
                <AlertTriangle size={14} className="warn" />
              )}
              {item.repositories.some((repo) => repo.dirty) && (
                <i className="dot" title="Uncommitted changes" />
              )}
              <small>{item.repositories.length}</small>
            </button>
          ))}
        </nav>
        {content}
      </div>
      {dialog?.kind === "clone" && (
        <CloneDialog
          busy={pending.has(DIALOG)}
          error={errors[DIALOG]}
          onClose={closeDialog}
          onSubmit={async (input) => {
            if (
              await run(
                DIALOG,
                () => api.repositories.clone({ projectId: project.id, ...input }),
                "Repository cloned.",
              )
            )
              closeDialog();
          }}
        />
      )}
      {dialog?.kind === "feature" && (
        <FeatureDialog
          feature={dialog.feature}
          repositories={repositories.filter(
            (repo) =>
              !repo.error &&
              !state.features
                .find((item) => item.name === dialog.feature)
                ?.repositories.some((entry) => entry.name === repo.name),
          )}
          busy={pending.has(DIALOG)}
          error={errors[DIALOG]}
          onClose={closeDialog}
          onSubmit={async (input) => {
            const before = new Set(state.features.map((item) => item.name));
            const value = await run(
              DIALOG,
              () =>
                api.repositories.createFeature({
                  projectId: project.id,
                  feature: dialog.feature,
                  date: isoDate(),
                  ...input,
                }),
              "Feature worktrees created.",
            );
            if (!value) return;
            const created =
              dialog.feature ?? value.features.find((item) => !before.has(item.name))?.name;
            if (created) setSelection({ kind: "feature", name: created });
            closeDialog();
          }}
        />
      )}
      {dialog?.kind === "switch" && (
        <SwitchBranchDialog
          repository={dialog.repo.name}
          current={dialog.repo.branch}
          branches={mainDefaults.get(dialog.repo.name)?.branches ?? []}
          busy={pending.has(DIALOG)}
          error={errors[DIALOG]}
          onClose={closeDialog}
          onSubmit={async (input) => {
            if (
              await run(
                DIALOG,
                () =>
                  api.repositories.switchBranch({
                    projectId: project.id,
                    feature: dialog.feature,
                    repository: dialog.repo.name,
                    ...input,
                  }),
                `${dialog.repo.name} switched to ${input.branch}.`,
              )
            )
              closeDialog();
          }}
        />
      )}
    </main>
  );
}

function Welcome({ onClone }: { onClone: () => void }) {
  return (
    <section className="panel welcome">
      <FolderGit2 size={32} />
      <h2>Develop features across repositories</h2>
      <p>
        Clone each repository on its default branch into the Project root, then create a feature to
        get one worktree per repository on the same <code>yyMMdd-feature</code> branch. Repositories
        you already cloned into the Project root appear here automatically.
      </p>
      <pre>{`.
├── .worktrees
│   └── 261127-add-extensions
│       ├── repo-a
│       └── repo-b
├── repo-a
└── repo-b`}</pre>
      <button className="primary" onClick={onClone}>
        <Download size={16} /> Clone a repository
      </button>
    </section>
  );
}

function Row({
  name,
  directory,
  busy,
  error,
  status,
  actions,
}: {
  name: string;
  directory: string;
  busy: boolean;
  error: string | undefined;
  status: ReactNode;
  actions: ReactNode;
}) {
  return (
    <li className="row" data-repository={name} aria-busy={busy}>
      <div className="row-main">
        <div className="row-title">
          <strong>{name}</strong>
          <div className="badges">{status}</div>
        </div>
        <div className="row-dir">
          <FolderOpen size={13} />
          <code>{directory}</code>
        </div>
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
      </div>
      <div className="actions">
        {busy ? <span className="spinner" aria-label="Working" /> : actions}
      </div>
    </li>
  );
}

function Badge({
  icon,
  tone,
  title,
  children,
}: {
  icon?: ReactNode;
  tone?: "warning" | "muted";
  title?: string;
  children: ReactNode;
}) {
  return (
    <span className={`badge${tone ? ` ${tone}` : ""}`} title={title}>
      {icon}
      {children}
    </span>
  );
}
