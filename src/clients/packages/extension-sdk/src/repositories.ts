/** 项目根目录的默认分支工作副本。A default-branch checkout at the project root. */
export interface WorkspaceRepository {
  name: string;
  defaultBranch: string;
  branch: string;
  dirty: boolean;
}

export interface FeatureRepository {
  name: string;
  branch: string;
  dirty: boolean;
  branches: string[];
}

export interface WorkspaceFeature {
  name: string;
  repositories: FeatureRepository[];
}

export interface RepositoryWorkspaceState {
  repositories: WorkspaceRepository[];
  features: WorkspaceFeature[];
}

export interface RepositoryWorkspaceApi {
  /** 这些方法需要 project.repositories；项目所有权由 Server 校验。Requires project.repositories; the Server checks project ownership. */
  get(projectId: string): Promise<RepositoryWorkspaceState>;
  clone(input: { projectId: string; name: string; url: string }): Promise<RepositoryWorkspaceState>;
  createFeature(input: {
    projectId: string;
    name: string;
    repositories: string[];
    /** 客户端当地日期，YYYY-MM-DD；省略时使用 UTC 日期。Client local date, YYYY-MM-DD; defaults to the UTC date. */
    date?: string;
    /** 向已有 feature 添加仓库；省略时创建新的 feature。Add repositories to an existing feature; otherwise create a new feature. */
    feature?: string;
  }): Promise<RepositoryWorkspaceState>;
  switchBranch(input: {
    projectId: string;
    name: string;
    feature: string;
    branch: string;
    createBranch?: boolean;
  }): Promise<RepositoryWorkspaceState>;
  /** 使用 --no-ff 合并所选 worktree 的 HEAD；冲突由用户处理。Merge the selected worktree HEAD with --no-ff; the user resolves conflicts. */
  merge(input: {
    projectId: string;
    name: string;
    feature: string;
  }): Promise<RepositoryWorkspaceState>;
}
