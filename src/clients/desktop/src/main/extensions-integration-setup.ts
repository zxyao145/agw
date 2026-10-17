// 在加载生产主进程之前设置 userData（单实例锁按 userData 区分）。
// Sets userData before the production main process loads (the single-instance lock is keyed by userData).
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { app } from "electron";

export type IntegrationConfig = {
  phase: "first" | "restart";
  userData: string;
  fixtures: string;
  projectDirectory: string;
  remoteProfile: { id: string; name: string; baseUrl: string };
  testServiceUrl: string;
  hostUrl: string;
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
