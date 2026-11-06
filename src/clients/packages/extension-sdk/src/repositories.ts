/** 项目目录或其子目录中的主工作副本。A main checkout in the Project directory or one of its subdirectories. */
export interface WorkspaceRepository {
  name: string;
  /** 相对 Project 的目录；空字符串表示 Project 根目录。The directory relative to the Project; empty for the Project root. */
  path: string;
  /** 远程默认分支；没有 origin/HEAD 时为当前分支。The remote default branch, or the current branch without origin/HEAD. */
  defaultBranch: string;
  /** 当前分支；分离 HEAD 时为空。The current branch; empty when HEAD is detached. */
  branch: string;
  dirty: boolean;
  /** 相对上游的提交数；没有上游时为 null。Commits relative to the upstream; null without one. */
  ahead: number | null;
  behind: number | null;
  branches: string[];
  /** 远程跟踪分支，例如 `origin/main`。Remote-tracking branches such as `origin/main`. */
  remoteBranches: string[];
  /** 读取失败的原因；非空时其他字段不可信。Why reading failed; other fields are unreliable when set. */
  error: string | null;
}

/** feature 中的 worktree。A worktree in a feature. */
export interface FeatureRepository {
  name: string;
  /** 相对 Project 的 worktree 目录。The worktree directory relative to the Project. */
  path: string;
  branch: string;
  dirty: boolean;
  /** 相对主工作副本默认分支的提交数。Commits relative to the root checkout's default branch. */
  ahead: number;
  behind: number;
  error: string | null;
}

export interface WorkspaceFeature {
  name: string;
  repositories: FeatureRepository[];
}

export interface RepositoryWorkspaceState {
  repositories: WorkspaceRepository[];
  features: WorkspaceFeature[];
}

/** 分支中的目录项；路径相对仓库根目录。A directory entry of a branch; the path is relative to the repository. */
export interface RepositoryTreeEntry {
  name: string;
  path: string;
  isDirectory: boolean;
}

export interface RepositoryTree {
  /**
   * 检出该分支的目录（相对 Project 根目录），此时条目来自磁盘；空字符串表示 Project 根目录，null 时条目来自提交。
   * Where the branch is checked out (relative to the Project root), with entries from the disk; an empty string
   * means the Project root, and null means the entries come from the commit.
   */
  checkout: string | null;
  entries: RepositoryTreeEntry[];
}

export type RepositoryChangeStatus =
  | "modified"
  | "added"
  | "deleted"
  | "renamed"
  | "untracked"
  | "conflicted";

export interface RepositoryChange {
  path: string;
  status: RepositoryChangeStatus;
}

/** 已提交的文件内容；二进制或过大时 content 为 null。Committed file content; null for binary or oversized files. */
export interface RepositoryBlob {
  content: string | null;
  size: number;
  isBinary: boolean;
  tooLarge: boolean;
}

export interface RepositoryWorkspaceApi {
  /** 这些方法需要 project.repositories；项目所有权由 Server 校验。Requires project.repositories; the Server checks project ownership. */
  get(projectId: string): Promise<RepositoryWorkspaceState>;
  /** `intoRoot` 默认为 false；子目录的 `name` 省略时从 URL 推导。`intoRoot` defaults to false; a subdirectory's `name` is derived from the URL when omitted. */
  clone(
    input: { projectId: string; url: string; name?: string; intoRoot?: boolean; proxyUrl?: string },
    options?: { signal?: AbortSignal },
  ): Promise<RepositoryWorkspaceState>;
  createFeature(input: {
    projectId: string;
    /** 新 feature 名称，前缀 `yyMMdd-`。The new feature name, prefixed with `yyMMdd-`. */
    name?: string;
    /** 向已有 feature 添加仓库；省略时创建新的 feature。Add repositories to an existing feature; otherwise create one. */
    feature?: string;
    /** 客户端当地日期，YYYY-MM-DD；省略时使用 UTC 日期。Client local date, YYYY-MM-DD; defaults to the UTC date. */
    date?: string;
    repositories: string[];
  }): Promise<RepositoryWorkspaceState>;
  /** 删除 feature 的 worktrees 及其 Git 登记信息，保留分支。Removes a feature's worktrees and Git registrations, preserving branches. */
  deleteFeature(input: { projectId: string; feature: string }): Promise<RepositoryWorkspaceState>;
  switchBranch(input: {
    projectId: string;
    feature: string;
    repository: string;
    branch: string;
    createBranch?: boolean;
  }): Promise<RepositoryWorkspaceState>;
  /** 使用 --no-ff 合并 worktree 的 HEAD；冲突时中止并报错。Merges the worktree HEAD with --no-ff; aborts and fails on conflicts. */
  merge(input: {
    projectId: string;
    feature: string;
    repository: string;
  }): Promise<RepositoryWorkspaceState>;
  /** fetch origin 并快进默认分支。Fetches origin and fast-forwards the default branch. */
  sync(input: {
    projectId: string;
    repository: string;
    proxyUrl?: string;
  }): Promise<RepositoryWorkspaceState>;
  /** 把主工作副本切回默认分支。Switches the root checkout back to its default branch. */
  restoreDefaultBranch(input: {
    projectId: string;
    repository: string;
  }): Promise<RepositoryWorkspaceState>;
  /** 本地或远程跟踪分支中一层目录；`path` 为空时为根目录。One directory level of a local or remote-tracking branch. */
  tree(input: {
    projectId: string;
    repository: string;
    branch: string;
    path?: string;
  }): Promise<RepositoryTree>;
  /** 检出该分支的目录中的未提交改动；未检出时为空。Uncommitted changes where the branch is checked out; empty otherwise. */
  changes(input: {
    projectId: string;
    repository: string;
    branch: string;
  }): Promise<RepositoryChange[]>;
  blob(input: {
    projectId: string;
    repository: string;
    branch: string;
    path: string;
  }): Promise<RepositoryBlob>;
}
