# Bare Repository

Open a Project in Desktop and select **Bare Repository** from the ribbon to show the **Repositories** sidebar. Repository operations run on that Project's Server and require the `project.repositories` extension permission. Git authentication and commit identity use the Server's existing Git configuration. The Project must use a local file system on the Server.

## Sidebar

Pick a repository and any local or remote-tracking branch:

- **Checked-out branches** are listed with their location: the main checkout or a feature worktree. The Files tab shows that directory as it is on disk. Selecting a file opens it in Desktop's File view, where it can be edited and its uncommitted diff shown with **Only diff**. The Changes tab lists the uncommitted changes, refreshed every few seconds while the sidebar is visible.
- **Other branches** are read-only. The Files tab shows the committed tree, and selecting a file opens its committed content in the read-only **Repository File** view. Binary files and files over 2 MiB are not shown.

The sidebar also offers the action for the selected location: **Merge** for a feature worktree, **Sync** for a main checkout on its default branch, and **Restore** for one that left it. **New feature**, **Clone** and **Manage** open the management view below.

## Management view

Repository and feature views are derived from the Project directory and Git:

- When the Project directory is a Git repository, the extension manages that single **main checkout**. Its name is the Project directory name, and its feature worktrees use `.worktrees/<feature>`. Clone is unavailable for that Project.
- Otherwise, each Project-root subdirectory with a `.git` directory is a **main checkout**, including repositories cloned outside the extension. Feature worktrees use `.worktrees/<feature>/<repo-name>`.
- A main checkout's default branch is `origin/HEAD`, or the current branch when that is missing.

Workflow:

1. **Clone** a repository by URL (HTTPS, `ssh://`, `user@host:path` or `git://`). The default destination is **Project subdirectory**, whose name defaults to the last URL segment without `.git`. Choose **Project root** to clone into an empty Project directory; this uses the Project directory name. Git checks out the remote default branch.
2. **New feature**: enter a name and select repositories. A Project-root repository is selected automatically. Your local date is prefixed as `yyMMdd-`, and each worktree starts on that branch from the local default branch. A Project-root repository uses `.worktrees/<yyMMdd>-<feature-name>`; repositories in subdirectories use `.worktrees/<yyMMdd>-<feature-name>/<repo-name>`. **Add repositories** appears when eligible repositories have no worktree in the selected feature. The first feature adds `/.worktrees/` to the root `.gitignore`.
3. Develop and commit in the worktrees with your usual tools. **Switch** moves a worktree to another branch, or creates one from its current HEAD.
4. **Merge** runs `git merge --no-ff` in the main checkout. If the merge conflicts, it is aborted and the main checkout is left unchanged. Merge the default branch into the feature branch in the worktree, resolve the conflicts there, and merge again. Configure Git `user.name` and `user.email` on the Server first.
5. **Sync** fetches `origin` and fast-forwards the default branch; **Sync all** does this for every main checkout.
6. **Delete feature** removes every worktree in the selected feature, its Git registration and the feature directory after confirmation. Git branches are kept. All worktrees are checked before deletion; commit or stash local changes, unlock locked worktrees and deinitialize submodules before deleting a feature.

A main checkout that is no longer on its default branch is flagged, and **Restore** switches it back when it has no local changes. Merge and Sync are disabled for that repository until it is restored. Switching and merging require clean working trees.

## Proxy

Open the **Bare Repository** management view using **Manage** in the Repositories sidebar, then select **Proxy** below **Main checkouts**. Enter a **Proxy URL**, for example `http://127.0.0.1:7890` or `socks5h://127.0.0.1:7890`, and select **Save**. Settings are saved in the extension's storage separately for each Server and Project. Clone, Sync and Sync all read the current Project's setting for each operation; the proxy address must be reachable from the Project's Server.

HTTP, HTTPS, SOCKS4, SOCKS4a, SOCKS5 and SOCKS5h proxy URLs can be used for HTTPS repositories. Git receives the proxy through command-specific `http.proxy` and `remote.origin.proxy` configuration. Leaving the field empty uses the Server's existing Git configuration. SSH and `git://` repositories use their existing Server transport configuration. See the [Git proxy documentation](https://raw.githubusercontent.com/git/git/master/Documentation/config/http.adoc) and [curl proxy URL formats](https://curl.se/libcurl/c/CURLOPT_PROXY.html).

Build from `src/clients` with `pnpm --filter @agw/extension-bare-repository build`. Desktop's bundled-extension build discovers this package and installs its `dist/` directory.

Run the proxy settings component tests from `src/clients` with `pnpm test:repository-proxy`. They use the extension SDK and Desktop's file system implementation to check saving, clearing, validation, independent settings for each Server and Project, and context changes. Server proxy transfer tests run with `dotnet test tests/Agw.Files.Tests` from the repository root.

Run `pnpm test:repository-workspace` from `src/clients` for Chromium component tests of Clone destinations, submitted parameters, cancellation and feature repository selection. These render the extension's dialogs and capture their submitted events.
