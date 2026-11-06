import type { WorkspaceRepository } from "@agw/extension-sdk";
import { X } from "lucide-react";
import { useEffect, useRef, useState, type FormEvent, type ReactNode } from "react";

/** 当地日期的 `yyMMdd`。The local date as `yyMMdd`. */
export function datePrefix(date = new Date()): string {
  return [date.getFullYear() % 100, date.getMonth() + 1, date.getDate()]
    .map((part) => String(part).padStart(2, "0"))
    .join("");
}

/** 当地日期的 `YYYY-MM-DD`。The local date as `YYYY-MM-DD`. */
export function isoDate(date = new Date()): string {
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, "0")}-${String(date.getDate()).padStart(2, "0")}`;
}

/** 与 Server 的推导规则一致：最后一段路径去掉 `.git`。Mirrors the Server: the last path segment without `.git`. */
function nameFromUrl(url: string): string {
  const last = url.trim().replace(/\/+$/u, "").split(/[/:]/u).at(-1) ?? "";
  return last.replace(/\.git$/iu, "");
}

function Modal({
  title,
  busy,
  cancellable = false,
  error,
  submitLabel,
  submitDisabled,
  onSubmit,
  onClose,
  children,
}: {
  title: string;
  busy: boolean;
  cancellable?: boolean;
  error: string | undefined;
  submitLabel: string;
  submitDisabled?: boolean;
  onSubmit: () => void;
  onClose: () => void;
  children: ReactNode;
}) {
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    ref.current?.showModal();
  }, []);
  function submit(event: FormEvent) {
    event.preventDefault();
    if (!busy) onSubmit();
  }
  return (
    <dialog
      ref={ref}
      onCancel={(event) => {
        event.preventDefault();
        if (!busy || cancellable) onClose();
      }}
    >
      <form onSubmit={submit} aria-busy={busy}>
        <header>
          <h2>{title}</h2>
          <button
            type="button"
            className="icon"
            aria-label="Close"
            disabled={busy && !cancellable}
            onClick={onClose}
          >
            <X size={16} />
          </button>
        </header>
        <fieldset disabled={busy}>{children}</fieldset>
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
        <footer>
          <button type="button" disabled={busy && !cancellable} onClick={onClose}>
            Cancel
          </button>
          <button type="submit" className="primary" disabled={busy || submitDisabled}>
            {busy ? "Working…" : submitLabel}
          </button>
        </footer>
      </form>
    </dialog>
  );
}

export function CloneDialog({
  busy,
  error,
  onSubmit,
  onClose,
}: {
  busy: boolean;
  error: string | undefined;
  onSubmit: (input: { url: string; name?: string; intoRoot: boolean }, signal: AbortSignal) => void;
  onClose: () => void;
}) {
  const [url, setUrl] = useState("");
  const [name, setName] = useState("");
  const [destination, setDestination] = useState("subdirectory");
  const operation = useRef<AbortController | null>(null);
  useEffect(() => () => operation.current?.abort(), []);
  function close() {
    operation.current?.abort();
    onClose();
  }
  return (
    <Modal
      title="Clone repository"
      busy={busy}
      cancellable
      error={error}
      submitLabel="Clone"
      submitDisabled={!url.trim()}
      onSubmit={() => {
        const controller = new AbortController();
        operation.current = controller;
        onSubmit(
          destination === "root"
            ? { url: url.trim(), intoRoot: true }
            : { url: url.trim(), name: name.trim() || undefined, intoRoot: false },
          controller.signal,
        );
      }}
      onClose={close}
    >
      <label>
        Repository URL
        <input
          name="url"
          required
          autoFocus
          value={url}
          onChange={(event) => setUrl(event.target.value)}
          placeholder="https://github.com/owner/repository.git"
        />
      </label>
      <label>
        Destination
        <select value={destination} onChange={(event) => setDestination(event.target.value)}>
          <option value="subdirectory">Project subdirectory</option>
          <option value="root">Project root</option>
        </select>
      </label>
      {destination === "root" ? (
        <small>Clones into the empty Project directory, using its directory name.</small>
      ) : (
        <label>
          Directory name
          <input
            name="name"
            pattern="[\p{L}\p{N}][\p{L}\p{N}._\-]*"
            value={name}
            onChange={(event) => setName(event.target.value)}
            placeholder={nameFromUrl(url) || "derived from the URL"}
          />
          <small>Clones the remote default branch into this directory of the Project.</small>
        </label>
      )}
    </Modal>
  );
}

export function FeatureDialog({
  feature,
  repositories,
  busy,
  error,
  onSubmit,
  onClose,
}: {
  /** 已有 feature 时只添加仓库。Only adds repositories when an existing feature is given. */
  feature?: string;
  repositories: WorkspaceRepository[];
  busy: boolean;
  error: string | undefined;
  onSubmit: (input: { name?: string; repositories: string[] }) => void;
  onClose: () => void;
}) {
  const [name, setName] = useState("");
  const singleRoot = repositories.length === 1 && repositories[0].path === "";
  const [selected, setSelected] = useState<ReadonlySet<string>>(
    () => new Set(singleRoot ? [repositories[0].name] : []),
  );
  const allSelected = repositories.length > 0 && selected.size === repositories.length;
  const branch = feature ?? `${datePrefix()}-${name.trim() || "feature-name"}`;
  function toggle(repo: string) {
    setSelected((previous) => {
      const next = new Set(previous);
      if (!next.delete(repo)) next.add(repo);
      return next;
    });
  }
  return (
    <Modal
      title={feature ? `Add repositories to ${feature}` : "New feature"}
      busy={busy}
      error={error}
      submitLabel={feature ? "Add worktrees" : "Create worktrees"}
      submitDisabled={selected.size === 0 || (!feature && !name.trim())}
      onSubmit={() =>
        onSubmit({ name: feature ? undefined : name.trim(), repositories: [...selected] })
      }
      onClose={onClose}
    >
      {!feature && (
        <label>
          Feature name
          <input
            name="name"
            required
            autoFocus
            pattern="[\p{L}\p{N}][\p{L}\p{N}._\-]*"
            value={name}
            onChange={(event) => setName(event.target.value)}
            placeholder="add-extensions"
          />
        </label>
      )}
      <p className="preview">
        Branch <code>{branch}</code> at{" "}
        <code>
          .worktrees/{branch}
          {singleRoot ? "" : "/<repo>"}
        </code>
      </p>
      <div className="choices" role="group" aria-label="Repositories">
        <label className="choice all">
          <input
            type="checkbox"
            checked={allSelected}
            disabled={repositories.length === 0}
            onChange={() =>
              setSelected(allSelected ? new Set() : new Set(repositories.map((repo) => repo.name)))
            }
          />
          Select all
        </label>
        {repositories.map((repo) => (
          <label className="choice" key={repo.name}>
            <input
              type="checkbox"
              name="repositories"
              value={repo.name}
              checked={selected.has(repo.name)}
              onChange={() => toggle(repo.name)}
            />
            <span>{repo.name}</span>
            <code>{repo.defaultBranch}</code>
          </label>
        ))}
        {repositories.length === 0 && <p>Every repository already has a worktree here.</p>}
      </div>
    </Modal>
  );
}

export function SwitchBranchDialog({
  repository,
  current,
  branches,
  busy,
  error,
  onSubmit,
  onClose,
}: {
  repository: string;
  current: string;
  branches: string[];
  busy: boolean;
  error: string | undefined;
  onSubmit: (input: { branch: string; createBranch: boolean }) => void;
  onClose: () => void;
}) {
  const [branch, setBranch] = useState("");
  const create = branch.trim() !== "" && !branches.includes(branch.trim());
  return (
    <Modal
      title={`Switch branch in ${repository}`}
      busy={busy}
      error={error}
      submitLabel={create ? "Create and switch" : "Switch"}
      submitDisabled={!branch.trim() || branch.trim() === current}
      onSubmit={() => onSubmit({ branch: branch.trim(), createBranch: create })}
      onClose={onClose}
    >
      <label>
        Branch
        <input
          name="branch"
          required
          autoFocus
          list="branch-options"
          value={branch}
          onChange={(event) => setBranch(event.target.value)}
          placeholder={current}
        />
        <datalist id="branch-options">
          {branches
            .filter((item) => item !== current)
            .map((item) => (
              <option key={item} value={item} />
            ))}
        </datalist>
        <small>
          {create
            ? `Creates ${branch.trim()} from the current HEAD.`
            : "Pick an existing branch or type a new name."}
        </small>
      </label>
    </Modal>
  );
}
