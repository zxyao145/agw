import type { ExtensionApi } from "@agw/extension-sdk";

import type {
  NotificationAnswerRequest,
  NotificationAnswerResult,
  TaskPilotDeliverySummary,
  TaskPilotMatterCancelRequest,
  TaskPilotMatterDetails,
  TaskPilotMatterSummary,
  TaskPilotReviewSummary,
  TaskPilotRoundDetails,
  TaskPilotRoundSummary,
  TaskPilotRuleDetails,
  TaskPilotRuleSummary,
  TaskPilotRuleWriteRequest,
  TaskPilotVersionRequest,
} from "./types";

export function createTaskPilotApi(extension: ExtensionApi) {
  const get = <T>(path: string, query?: Record<string, string | number | boolean>) =>
    extension.server.request<T>(path, { method: "GET", ...(query ? { query } : {}) });
  const post = <TRequest, TResponse>(path: string, body: TRequest) =>
    extension.server.request<TResponse>(path, { method: "POST", body });

  return {
    listRules: () => get<TaskPilotRuleSummary[]>("rules/list"),
    getRule: (id: string) => get<TaskPilotRuleDetails>("rules/get", { id }),
    createRule: (request: TaskPilotRuleWriteRequest) =>
      post<TaskPilotRuleWriteRequest, TaskPilotRuleDetails>("rules/create", request),
    updateRule: (request: TaskPilotRuleWriteRequest) =>
      post<TaskPilotRuleWriteRequest, TaskPilotRuleDetails>("rules/update", request),
    deleteRule: (request: TaskPilotVersionRequest) =>
      post<TaskPilotVersionRequest, TaskPilotRuleDetails>("rules/delete", request),
    pauseRule: (request: TaskPilotVersionRequest) =>
      post<TaskPilotVersionRequest, TaskPilotRuleDetails>("rules/pause", request),
    resumeRule: (request: TaskPilotVersionRequest) =>
      post<TaskPilotVersionRequest, TaskPilotRuleDetails>("rules/resume", request),
    listMatters: (ruleId?: string) =>
      get<TaskPilotMatterSummary[]>("matters/list", ruleId ? { ruleId } : undefined),
    getMatter: (id: string) => get<TaskPilotMatterDetails>("matters/get", { id }),
    pauseMatter: (request: TaskPilotVersionRequest) =>
      post<TaskPilotVersionRequest, TaskPilotMatterSummary>("matters/pause", request),
    resumeMatter: (request: TaskPilotVersionRequest) =>
      post<TaskPilotVersionRequest, TaskPilotMatterSummary>("matters/resume", request),
    runMatter: (request: TaskPilotVersionRequest) =>
      post<TaskPilotVersionRequest, TaskPilotRoundSummary>("matters/run", request),
    cancelMatter: (request: TaskPilotMatterCancelRequest) =>
      post<TaskPilotMatterCancelRequest, TaskPilotRoundSummary>("matters/cancel", request),
    listRounds: (matterId: string) => get<TaskPilotRoundSummary[]>("rounds/list", { matterId }),
    getRound: (id: string) => get<TaskPilotRoundDetails>("rounds/get", { id }),
    listReviews: (matterId: string) => get<TaskPilotReviewSummary[]>("reviews/list", { matterId }),
    cancelReview: (request: TaskPilotVersionRequest) =>
      post<TaskPilotVersionRequest, TaskPilotReviewSummary>("reviews/cancel", request),
    getDeliveries: (matterId: string) =>
      get<TaskPilotDeliverySummary[]>("deliveries/get", { matterId }),
    answerNotification: (request: NotificationAnswerRequest) =>
      extension.server.request<NotificationAnswerResult>("/api/notifications/answer", {
        method: "POST",
        body: request,
      }),
  };
}

export type TaskPilotApi = ReturnType<typeof createTaskPilotApi>;
