import type {
  ContextProject,
  ExtensionApi,
  FeatureRepository,
  RepositoryWorkspaceState,
  WorkspaceFeature,
} from "@agw/extension-sdk";
import { useEffect, useRef, useState, type FormEvent } from "react";

export function ProjectWorkspace({ api, project }: { api: ExtensionApi; project: ContextProject }) {
  const [state, setState] = useState<RepositoryWorkspaceState>({ repositories: [], features: [] });
  const [busy, setBusy] = useState(true);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [feature, setFeature] = useState("");
  const active = useRef(true);
  useEffect(() => {
    active.current = true;
    void api.repositories
      .get(project.id)
      .then((value) => {
        if (active.current) setState(value);
      })
      .catch((caught: unknown) => {
        if (active.current) setError(String(caught));
      })
      .finally(() => {
        if (active.current) setBusy(false);
      });
    return () => {
      active.current = false;
    };
  }, [api, project.id]);

  async function run(operation: () => Promise<RepositoryWorkspaceState>, message = "") {
    if (busy || !active.current) return;
    setBusy(true);
    setError("");
    setNotice("");
    try {
      const value = await operation();
      if (active.current) {
        setState(value);
        setNotice(message);
      }
    } catch (caught) {
      if (active.current) setError(caught instanceof Error ? caught.message : String(caught));
      try {
        const value = await api.repositories.get(project.id);
        if (active.current) setState(value);
      } catch (refreshError) {
        if (active.current) setError((previous) => `${previous}\n${String(refreshError)}`);
      }
    } finally {
      if (active.current) setBusy(false);
    }
  }

  function clone(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const data = new FormData(event.currentTarget);
    void run(
      () =>
        api.repositories.clone({
          projectId: project.id,
          name: String(data.get("name")).trim(),
          url: String(data.get("url")).trim(),
        }),
      "Repository cloned on its default branch.",
    );
  }

  function createFeature(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const data = new FormData(event.currentTarget);
    const today = new Date();
    const date = `${today.getFullYear()}-${String(today.getMonth() + 1).padStart(2, "0")}-${String(today.getDate()).padStart(2, "0")}`;
    void run(
      () =>
        api.repositories.createFeature({
          projectId: project.id,
          date,
          name: String(data.get("name") ?? "feature").trim(),
          feature: feature || undefined,
          repositories: data.getAll("repositories").map(String),
        }),
      "Feature worktrees created.",
    );
  }

  async function merge(item: WorkspaceFeature, repo: FeatureRepository) {
    const main = state.repositories.find((entry) => entry.name === repo.name)!;
    const decision = await api.dialog.showMessageBox({
      type: "warning",
      message: `Merge ${repo.branch} into ${repo.name}/${main.defaultBranch}?`,
      detail:
        "Creates a merge commit in the root checkout. Conflicts remain there for you to resolve.",
      buttons: ["Cancel", "Merge"],
      defaultId: 0,
      cancelId: 0,
    });
    if (decision === 1)
      await run(
        () =>
          api.repositories.merge({ projectId: project.id, feature: item.name, name: repo.name }),
        `Merged ${repo.name} into ${main.defaultBranch}.`,
      );
  }

  return (
    <main aria-busy={busy}>
      <header>
        <div>
          <h1>Bare Repository</h1>
          <p>{project.name}</p>
        </div>
        <button disabled={busy} onClick={() => void run(() => api.repositories.get(project.id))}>
          Refresh
        </button>
      </header>
      <p>
        Root checkouts stay on their default branches. Develop in{" "}
        <code>.worktrees/yyMMdd-feature/repository</code> and merge each repository when ready.
      </p>
      {error && <pre role="alert">{error}</pre>}
      <p role="status">{busy ? "Working…" : notice}</p>
      <fieldset disabled={busy}>
        <section>
          <h2>Repositories</h2>
          {state.repositories.length === 0 && <p>No managed repositories yet.</p>}
          <ul>
            {state.repositories.map((repo) => (
              <li key={repo.name}>
                <strong>{repo.name}</strong>
                <code>{repo.defaultBranch}</code>
                <span>Default branch locked{repo.dirty ? " · Local changes" : ""}</span>
              </li>
            ))}
          </ul>
          <form onSubmit={clone}>
            <label>
              Repository name
              <input
                name="name"
                required
                pattern="[A-Za-z0-9][A-Za-z0-9._-]*"
                placeholder="module-repository"
              />
            </label>
            <label>
              Repository URL
              <input name="url" required placeholder="https://github.com/owner/repository.git" />
            </label>
            <button type="submit">Clone repository</button>
          </form>
        </section>
        <section>
          <h2>Create feature worktrees</h2>
          <form onSubmit={createFeature}>
            <label>
              Feature
              <select value={feature} onChange={(event) => setFeature(event.target.value)}>
                <option value="">New feature</option>
                {state.features.map((item) => (
                  <option key={item.name}>{item.name}</option>
                ))}
              </select>
            </label>
            {!feature && (
              <label>
                Feature name
                <input name="name" required placeholder="add-extensions" />
                <small>Your local date is prefixed as yyMMdd.</small>
              </label>
            )}
            <div className="choices">
              {state.repositories
                .filter(
                  (repo) =>
                    !state.features
                      .find((item) => item.name === feature)
                      ?.repositories.some((entry) => entry.name === repo.name),
                )
                .map((repo) => (
                  <label key={repo.name}>
                    <input type="checkbox" name="repositories" value={repo.name} />
                    {repo.name}
                  </label>
                ))}
            </div>
            <button type="submit" disabled={state.repositories.length === 0}>
              Create worktrees
            </button>
          </form>
        </section>
        <section>
          <h2>Features</h2>
          {state.features.length === 0 && <p>No features yet.</p>}
          {state.features.map((item) => (
            <article key={item.name}>
              <h3>{item.name}</h3>
              {item.repositories.map((repo) => (
                <div className="worktree" key={repo.name}>
                  <strong>{repo.name}</strong>
                  <code>
                    .worktrees/{item.name}/{repo.name}
                  </code>
                  <p>
                    Branch: {repo.branch}
                    {repo.dirty ? " · Local changes" : ""}
                  </p>
                  <form
                    onSubmit={(event) => {
                      event.preventDefault();
                      const data = new FormData(event.currentTarget);
                      void run(
                        () =>
                          api.repositories.switchBranch({
                            projectId: project.id,
                            feature: item.name,
                            name: repo.name,
                            branch: String(data.get("branch")).trim(),
                            createBranch: data.has("create"),
                          }),
                        "Worktree branch changed.",
                      );
                    }}
                  >
                    <label>
                      Branch
                      <input
                        name="branch"
                        required
                        list={`${item.name}-${repo.name}-branches`}
                        defaultValue={repo.branch}
                      />
                      <datalist id={`${item.name}-${repo.name}-branches`}>
                        {repo.branches.map((branch) => (
                          <option key={branch} value={branch} />
                        ))}
                      </datalist>
                    </label>
                    <label className="checkbox">
                      <input type="checkbox" name="create" />
                      Create new branch
                    </label>
                    <button type="submit" disabled={repo.dirty}>
                      Switch branch
                    </button>
                  </form>
                  <button
                    disabled={
                      repo.dirty ||
                      state.repositories.find((entry) => entry.name === repo.name)?.dirty
                    }
                    onClick={() => void merge(item, repo)}
                  >
                    Merge into default branch
                  </button>
                </div>
              ))}
            </article>
          ))}
        </section>
      </fieldset>
    </main>
  );
}
