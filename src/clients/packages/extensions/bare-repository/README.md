# Bare Repository

Open a Project in Desktop and select **Bare Repository** from the ribbon to show the **Repositories** sidebar. Repository operations run on that Project's Server and require the `project.repositories` extension permission. Git authentication and commit identity use the Server's existing Git configuration. The Project must use a local file system on the Server.

## Sidebar

Pick a repository and any local or remote-tracking branch:

- **Checked-out branches** are listed with their location: the main checkout or a feature worktree. The Files tab shows that directory as it is on disk. Selecting a file opens it in Desktop's File view, where it can be edited and its uncommitted diff shown with **Only diff**. The Changes tab lists the uncommitted changes, refreshed every few seconds while the sidebar is visible.
- **Other branches** are read-only. The Files tab shows the committed tree, and selecting a file opens its committed content in the read-only **Repository File** view. Binary files and files over 2 MiB are not shown.

The sidebar also offers the action for the selected location: **Merge** for a feature worktree, **Sync** for a main checkout on its default branch, and **Restore** for one that left it. **New feature**, **Clone** and **Manage** open the management view below.

## Management view

The extension follows a directory convention; it does not enforce it. Nothing is stored besides the Git repositories themselves. Every view is derived from the disk:

- Each Project-root subdirectory with a `.git` directory is a **main checkout**, including repositories cloned outside the extension. Its default branch is `origin/HEAD`, or the current branch when that is missing.
- Each `.worktrees/<feature>/<repo-name>` directory is a feature worktree of `<repo-name>`.

Workflow:

1. **Clone** a repository by URL (HTTPS, `ssh://`, `user@host:path` or `git://`). The directory name defaults to the last URL segment without `.git`. Git checks out the remote default branch.
2. **New feature**: enter a name and select repositories. Your local date is prefixed as `yyMMdd-`. Each worktree starts on that branch from the local default branch at `.worktrees/<yyMMdd>-<feature-name>/<repo-name>`. **Add repositories** adds worktrees to an existing feature. The first feature adds `/.worktrees/` to the root `.gitignore`.
3. Develop and commit in the worktrees with your usual tools. **Switch** moves a worktree to another branch, or creates one from its current HEAD.
4. **Merge** runs `git merge --no-ff` in the main checkout. If the merge conflicts, it is aborted and the main checkout is left unchanged. Merge the default branch into the feature branch in the worktree, resolve the conflicts there, and merge again. Configure Git `user.name` and `user.email` on the Server first.
5. **Sync** fetches `origin` and fast-forwards the default branch; **Sync all** does this for every main checkout.

A main checkout that is no longer on its default branch is flagged, and **Restore** switches it back when it has no local changes. Merge and Sync are disabled for that repository until it is restored. Switching and merging require clean working trees.

Build from `src/clients` with `pnpm --filter @agw/extension-bare-repository build`. Desktop's bundled-extension build discovers this package and installs its `dist/` directory.
