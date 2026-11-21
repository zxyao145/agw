export type JsonValue =
  | string
  | number
  | boolean
  | null
  | JsonValue[]
  | { [key: string]: JsonValue };

export type TaskPilotDataScope = {
  eventPaths: Record<string, string[]>;
  maximumBytes: number;
};

export type TaskPilotRetryPolicy = {
  delayMinutes: number[];
  maximumDelayMinutes: number;
  notificationThreshold: number;
  stopThreshold: number;
};

export type TaskPilotRuleSummary = {
  id: string;
  name: string;
  kind: number;
  enabled: boolean;
  paused: boolean;
  deletedAt: string | null;
  version: number;
  subscriptionId: string | null;
  subscriptionPending: boolean;
  subscriptionError: string | null;
};

export type TaskPilotRuleDetails = {
  rule: TaskPilotRuleSummary;
  sourceIds: string[];
  eventTypes: string[];
  condition: JsonValue | null;
  matterKey: JsonValue | null;
  targetKind: number;
  targetId: string | null;
  projectId: string;
  instructions: string;
  skill: string | null;
  dataScope: TaskPilotDataScope;
  permissionMode: number;
  askFirst: string[];
  groupKey: string;
  maxConcurrency: number;
  sessionPolicy: number;
  channels: string[];
  notifyPolicy: number;
  emitTypes: string[];
  retry: TaskPilotRetryPolicy;
  delivery: JsonValue | null;
};

export type TaskPilotRuleWriteRequest = Omit<TaskPilotRuleDetails, "rule" | "groupKey"> & {
  id?: string;
  version?: number;
  name: string;
  kind: number;
  enabled: boolean;
  groupKey: string | null;
};

export type TaskPilotVersionRequest = { id: string; version: number };
export type TaskPilotMatterCancelRequest = TaskPilotVersionRequest & { roundId?: string };

export type TaskPilotMatterSummary = {
  id: string;
  ruleId: string;
  matterKey: string;
  title: string;
  link: string | null;
  state: number;
  conversationId: string | null;
  consecutiveFailures: number;
  version: number;
  queuedRounds: number;
  activeRoundId: string | null;
};

export type TaskPilotCardSummary = {
  id: string;
  roundId: string;
  kind: number;
  cardKey: string;
  title: string;
  text: string;
  options: { label: string; value: string }[];
  recommend: number | null;
  about: string | null;
  state: number;
  notificationId: string | null;
  conversationId: string | null;
  answer: JsonValue | null;
  resolvedReason: string | null;
  allowText: boolean;
};

export type TaskPilotMatterDetails = {
  matter: TaskPilotMatterSummary;
  cards: TaskPilotCardSummary[];
};

export type TaskPilotRoundSummary = {
  id: string;
  matterId: string;
  state: number;
  trigger: number;
  headEventId: string | null;
  conversationId: string | null;
  turnId: string | null;
  attempt: number;
  outcome: number | null;
  summary: string | null;
  receiptError: string | null;
  error: string | null;
  inputTokens: number;
  outputTokens: number;
  notBefore: string | null;
  startedAt: string | null;
  endedAt: string | null;
  version: number;
};

export type TaskPilotRoundInput = {
  eventId: string;
  kind: number;
  event: JsonValue | null;
  payload: JsonValue | null;
  receivedAt: string;
  chainId: string | null;
  parentEventId: string | null;
  hops: number;
  summary: string | null;
  link: string | null;
};

export type TaskPilotRoundAttempt = {
  attempt: number;
  turnId: string;
  purpose: number;
  state: number;
  error: string | null;
  inputTokens: number;
  outputTokens: number;
  startedAt: string | null;
  endedAt: string | null;
};

export type TaskPilotRoundDetails = {
  round: TaskPilotRoundSummary;
  inputs: TaskPilotRoundInput[];
  attempts: TaskPilotRoundAttempt[];
  receipt: JsonValue | null;
};

export type TaskPilotReviewSummary = {
  id: string;
  matterId: string;
  reviewKey: string;
  dueAt: string | null;
  about: string | null;
  state: number;
  requestSeq: number;
  version: number;
};

export type TaskPilotDeliverySummary = {
  id: string;
  matterId: string;
  repository: string;
  pullRequestNumber: number;
  mode: number;
  stage: string;
  headSha: string | null;
  reviewedSha: string | null;
  mergeSha: string | null;
  lastError: string | null;
  version: number;
};

export type NotificationAnswerRequest = {
  notificationId: string;
  option: number | null;
  text: string | null;
};

export type NotificationAnswerResult = {
  notificationId: string;
  state: number;
  version: number;
  option: number | null;
  text: string | null;
  answeredBy: string | null;
  answeredVia: string | null;
  answeredAt: string | null;
};
