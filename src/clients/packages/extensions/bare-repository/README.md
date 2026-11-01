# Bare Repository

Open a Project in Desktop and select **Bare Repository** from the ribbon. Repository operations run on that Project's Server and require the `project.repositories` extension permission. Git authentication and commit identity use the Server's existing Git configuration.

1. Clone each repository by URL and directory name. Git checks out the remote default branch into `<project>/<repo-name>`.
2. Enter a feature name and select its repositories. Your local date is prefixed as `yyMMdd-`; each worktree starts on that branch at `.worktrees/<yyMMdd>-<feature-name>/<repo-name>`. Select an existing feature to add further repositories. API calls can pass an ISO `date`; the default is the UTC date.
3. Develop and commit within the worktrees using the project's normal development tools. The extension can switch a worktree to another existing branch or create a branch from its current HEAD.
4. Use **Merge into default branch** for each repository. The Server uses `git merge --no-ff`, preserving a merge commit. Configure Git `user.name` and `user.email` on the Server before merging.

Root checkouts stay on the default branch recorded at clone time. The extension and repository API expose branch switching only for registered feature worktrees and reject operations if a root checkout has changed branches. Terminal Git commands follow normal Git behavior.

Commit or move local changes before switching or merging. Merge conflicts remain in the root checkout for resolution with Git; finish or abort that merge before starting another operation. Multi-repository merges are performed separately. When creating several worktrees, each successful repository is saved immediately; after a failure, refresh and add the remaining repositories to the existing feature.

The extension preserves existing root `.gitignore` entries and adds `/.worktrees/` and `/.agw/`. Its repository catalog lives in `.agw/bare-repositories.json`. Existing directories are preserved and duplicate destinations are rejected. Keep the catalog with the workspace when moving or backing up the Project.

Build from `src/clients` with `pnpm --filter @agw/extension-bare-repository build`. Desktop's bundled-extension build discovers this package and installs its `dist/` directory.
