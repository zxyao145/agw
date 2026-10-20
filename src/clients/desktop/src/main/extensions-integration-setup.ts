// 在加载生产主进程之前设置 userData（单实例锁按 userData 区分）。
// Sets userData before the production main process loads (the single-instance lock is keyed by userData).
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { app } from "electron";

export type IntegrationConfig = {
  phase: "first" | "restart" | "performance";
  userData: string;
  fixtures: string;
  projectDirectory: string;
  remoteProfile: { id: string; name: string; baseUrl: string };
  testServiceUrl: string;
  hostUrl: string;
  /** 性能测试阶段的输入，路径相对 Project 目录；其他阶段为 null。Performance inputs relative to the Project; null in other phases. */
  performance: {
    markdown: { path: string; kind: string; kib: number }[];
    code: string;
  } | null;
  /** 性能测试结果写入的文件。The file the performance results are written to. */
  results: string;
  firstToken: string;
  secondToken: string;
  agentId: string;
  agentName: string;
  projectId: string;
};

export const config = JSON.parse(
  readFileSync(resolve(".scratch/extension-tests/config.json"), "utf8"),
) as IntegrationConfig;

app.setPath("userData", config.userData);
