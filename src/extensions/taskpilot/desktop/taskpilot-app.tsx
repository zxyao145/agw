import * as React from "react";
import type { ContextProject, ContextTarget, ExtensionApi } from "@agw/extension-sdk";

import { createTaskPilotApi } from "./server-api";
import type {
  JsonValue,
  TaskPilotCardSummary,
  TaskPilotDataScope,
  TaskPilotDeliverySummary,
  TaskPilotMatterDetails,
  TaskPilotMatterSummary,
  TaskPilotReviewSummary,
  TaskPilotRoundDetails,
  TaskPilotRoundSummary,
  TaskPilotRuleDetails,
  TaskPilotRuleSummary,
  TaskPilotRuleWriteRequest,
  TaskPilotRetryPolicy,
} from "./types";

type Section = "rules" | "matters" | "rounds" | "reviews" | "deliveries";
type Feedback = { kind: "success" | "error"; message: string };

type RuleFormState = {
  name: string;
  kind: string;
  enabled: boolean;
  sourceIds: string;
  eventTypes: string;
  condition: string;
  matterKey: string;
  targetKind: string;
  targetId: string;
  projectId: string;
  instructions: string;
  skill: string;
  dataScope: string;
  permissionMode: string;
  askFirst: string;
  groupKey: string;
  maxConcurrency: string;
  sessionPolicy: string;
  channels: string;
  notifyPolicy: string;
  emitTypes: string;
  retry: string;
  delivery: string;
};

const EMPTY_DATA_SCOPE: TaskPilotDataScope = { eventPaths: {}, maximumBytes: 16000 };
const EMPTY_RETRY_POLICY: TaskPilotRetryPolicy = {
  delayMinutes: [2, 4, 8, 16, 32],
  maximumDelayMinutes: 60,
  notificationThreshold: 3,
  stopThreshold: 5,
};

const EMPTY_RULE_FORM: RuleFormState = {
  name: "",
  kind: "0",
  enabled: true,
  sourceIds: "",
  eventTypes: "",
  condition: "",
  matterKey: "",
  targetKind: "0",
  targetId: "",
  projectId: "",
  instructions: "",
  skill: "",
  dataScope: JSON.stringify(EMPTY_DATA_SCOPE, null, 2),
  permissionMode: "0",
  askFirst: "",
  groupKey: "",
  maxConcurrency: "1",
  sessionPolicy: "0",
  channels: "",
  notifyPolicy: "1",
  emitTypes: "",
  retry: JSON.stringify(EMPTY_RETRY_POLICY, null, 2),
  delivery: "",
};

const SECTIONS: { id: Section; label: string }[] = [
  { id: "rules", label: "Rules" },
  { id: "matters", label: "Matters" },
  { id: "rounds", label: "Rounds" },
  { id: "reviews", label: "Reviews" },
  { id: "deliveries", label: "PR delivery" },
];

const RULE_KINDS = ["Agent", "Delivery"];
const MATTER_STATES = ["Active", "Waiting for a person", "Paused", "Stopped"];
const ROUND_STATES = [
  "Queued",
  "Submitting",
  "Running",
  "Repairing receipt",
  "Cancelling",
  "Completed",
  "Failed",
  "Cancelled",
];
const ROUND_TRIGGERS = ["Event", "Answer", "Review", "Retry", "Manual"];
const ROUND_INPUT_KINDS = ["Event", "Answer", "Review", "Retry", "Manual"];
const CARD_KINDS = ["Question", "Notice"];
const CARD_STATES = ["Open", "Answered", "Resolved", "Closed"];
const REVIEW_STATES = ["Scheduled", "Due", "Cancelled"];

function errorText(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function enumLabel(value: number, labels: readonly string[]): string {
  return labels[value] ?? `Unknown (${value})`;
}

function dateLabel(value: string | null): string {
  if (!value) return "Not recorded";
  const date = new Date(value);
  if (!Number.isFinite(date.getTime())) return value;
  return new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(
    date,
  );
}

function jsonText(value: JsonValue | null): string {
  return value === null ? "" : JSON.stringify(value, null, 2);
}

function prettyJson(value: JsonValue | null): string {
  return value === null ? "No data" : JSON.stringify(value, null, 2);
}

function splitValues(value: string): string[] {
  return value
    .split(/[\n,]/u)
    .map((item) => item.trim())
    .filter(Boolean);
}

function parseJson(value: string, label: string): JsonValue | null {
  if (!value.trim()) return null;
  try {
    return JSON.parse(value) as JsonValue;
  } catch (error) {
    throw new Error(`${label}: ${errorText(error)}`);
  }
}

function parseInteger(value: string, label: string): number {
  const parsed = Number(value);
  if (!Number.isSafeInteger(parsed)) throw new Error(`${label} must be a whole number.`);
  return parsed;
}

function formFromRule(details: TaskPilotRuleDetails): RuleFormState {
  return {
    name: details.rule.name,
    kind: String(details.rule.kind),
    enabled: details.rule.enabled,
    sourceIds: details.sourceIds.join(", "),
    eventTypes: details.eventTypes.join("\n"),
    condition: jsonText(details.condition),
    matterKey: jsonText(details.matterKey),
    targetKind: String(details.targetKind),
    targetId: details.targetId ?? "",
    projectId: details.projectId,
    instructions: details.instructions,
    skill: details.skill ?? "",
    dataScope: JSON.stringify(details.dataScope, null, 2),
    permissionMode: String(details.permissionMode),
    askFirst: details.askFirst.join(", "),
    groupKey: details.groupKey,
    maxConcurrency: String(details.maxConcurrency),
    sessionPolicy: String(details.sessionPolicy),
    channels: details.channels.join(", "),
    notifyPolicy: String(details.notifyPolicy),
    emitTypes: details.emitTypes.join("\n"),
    retry: JSON.stringify(details.retry, null, 2),
    delivery: jsonText(details.delivery),
  };
}

function requestFromRuleForm(
  form: RuleFormState,
  details: TaskPilotRuleDetails | null,
): TaskPilotRuleWriteRequest {
  let dataScope: TaskPilotDataScope;
  let retry: TaskPilotRetryPolicy;
  try {
    dataScope = JSON.parse(form.dataScope) as TaskPilotDataScope;
  } catch (error) {
    throw new Error(`Data scope: ${errorText(error)}`);
  }
  try {
    retry = JSON.parse(form.retry) as TaskPilotRetryPolicy;
  } catch (error) {
    throw new Error(`Retry policy: ${errorText(error)}`);
  }
  if (!form.name.trim()) throw new Error("Rule name is required.");
  if (!form.projectId.trim()) throw new Error("Project ID is required.");
  if (!dataScope || typeof dataScope !== "object" || Array.isArray(dataScope)) {
    throw new Error("Data scope must be a JSON object.");
  }
  if (!retry || typeof retry !== "object" || Array.isArray(retry)) {
    throw new Error("Retry policy must be a JSON object.");
  }

  return {
    ...(details ? { id: details.rule.id, version: details.rule.version } : {}),
    name: form.name.trim(),
    kind: parseInteger(form.kind, "Rule kind"),
    enabled: form.enabled,
    sourceIds: splitValues(form.sourceIds),
    eventTypes: splitValues(form.eventTypes),
    condition: parseJson(form.condition, "Condition"),
    matterKey: parseJson(form.matterKey, "Matter key"),
    targetKind: parseInteger(form.targetKind, "Target kind"),
    targetId: form.targetId.trim() || null,
    projectId: form.projectId.trim(),
    instructions: form.instructions,
    skill: form.skill.trim() || null,
    dataScope,
    permissionMode: parseInteger(form.permissionMode, "Permission mode"),
    askFirst: splitValues(form.askFirst),
    groupKey: form.groupKey.trim() || null,
    maxConcurrency: parseInteger(form.maxConcurrency, "Maximum concurrency"),
    sessionPolicy: parseInteger(form.sessionPolicy, "Session policy"),
    channels: splitValues(form.channels),
    notifyPolicy: parseInteger(form.notifyPolicy, "Notification policy"),
    emitTypes: splitValues(form.emitTypes),
    retry,
    delivery: parseJson(form.delivery, "Delivery configuration"),
  };
}

function Field({
  label,
  hint,
  children,
}: {
  label: string;
  hint?: string;
  children: React.ReactNode;
}) {
  return (
    <label className="tp-field">
      <span className="tp-field-label">{label}</span>
      {children}
      {hint ? <small>{hint}</small> : null}
    </label>
  );
}

function Badge({ children, tone = "neutral" }: { children: React.ReactNode; tone?: string }) {
  return <span className={`tp-badge tp-badge-${tone}`}>{children}</span>;
}

function RuleStatus({ rule }: { rule: TaskPilotRuleSummary }) {
  if (rule.deletedAt) return <Badge tone="muted">Deleted</Badge>;
  if (rule.subscriptionError) return <Badge tone="danger">Subscription error</Badge>;
  if (rule.subscriptionPending) return <Badge tone="warning">Syncing subscription</Badge>;
  if (rule.subscriptionId) return <Badge tone="success">Subscription active</Badge>;
  return <Badge tone="muted">Subscription pending</Badge>;
}

function RuleForm({
  value,
  onChange,
  onSubmit,
  onCancel,
  editing,
  disabled,
  projects,
  targets,
}: {
  value: RuleFormState;
  onChange: (change: Partial<RuleFormState>) => void;
  onSubmit: (event: React.FormEvent<HTMLFormElement>) => void;
  onCancel: () => void;
  editing: boolean;
  disabled: boolean;
  projects: ContextProject[];
  targets: ContextTarget[];
}) {
  const targetType = value.targetKind === "0" ? "agent" : "agentflow";
  const availableTargets = targets.filter((target) => target.type === targetType);
  return (
    <form className="tp-form" onSubmit={onSubmit}>
      <div className="tp-form-heading">
        <div>
          <p className="tp-eyebrow">{editing ? "Rule configuration" : "New rule"}</p>
          <h2>{editing ? value.name || "Edit rule" : "Create a rule"}</h2>
          <p>Rules subscribe to events and define the execution policy for each matter.</p>
        </div>
        <label className="tp-switch">
          <input
            type="checkbox"
            checked={value.enabled}
            onChange={(event) => onChange({ enabled: event.target.checked })}
          />
          <span>Enabled</span>
        </label>
      </div>

      <section className="tp-form-section">
        <h3>Event matching</h3>
        <div className="tp-form-grid">
          <Field label="Name">
            <input
              value={value.name}
              onChange={(event) => onChange({ name: event.target.value })}
              required
            />
          </Field>
          <Field label="Rule kind">
            <select value={value.kind} onChange={(event) => onChange({ kind: event.target.value })}>
              <option value="0">Agent</option>
              <option value="1">Delivery</option>
            </select>
          </Field>
          <Field label="Event source IDs" hint="Comma separated source IDs.">
            <input
              value={value.sourceIds}
              onChange={(event) => onChange({ sourceIds: event.target.value })}
            />
          </Field>
          <Field label="Event types" hint="One event type per line or comma separated.">
            <textarea
              rows={3}
              value={value.eventTypes}
              onChange={(event) => onChange({ eventTypes: event.target.value })}
            />
          </Field>
          <Field
            label="Condition (JSONLogic)"
            hint="Leave empty to match every event of the selected types."
          >
            <textarea
              className="tp-code-input"
              rows={4}
              value={value.condition}
              onChange={(event) => onChange({ condition: event.target.value })}
              spellCheck={false}
            />
          </Field>
          <Field label="Matter key (JSONLogic)" hint="Leave empty to use the event subject.">
            <textarea
              className="tp-code-input"
              rows={4}
              value={value.matterKey}
              onChange={(event) => onChange({ matterKey: event.target.value })}
              spellCheck={false}
            />
          </Field>
        </div>
      </section>

      <section className="tp-form-section">
        <h3>Execution</h3>
        <div className="tp-form-grid">
          <Field label="Target kind">
            <select
              value={value.targetKind}
              onChange={(event) => onChange({ targetKind: event.target.value, targetId: "" })}
            >
              <option value="0">Agent</option>
              <option value="1">Agentflow</option>
            </select>
          </Field>
          <Field label="Target ID">
            <select
              value={value.targetId}
              required={value.kind === "0"}
              onChange={(event) => onChange({ targetId: event.target.value })}
            >
              <option value="">
                {value.kind === "1" ? "Not required for Delivery rules" : "Choose a target"}
              </option>
              {availableTargets.map((target) => (
                <option key={target.id} value={target.id}>
                  {target.displayName || target.name}
                </option>
              ))}
            </select>
          </Field>
          <Field label="Project ID">
            <select
              value={value.projectId}
              onChange={(event) => onChange({ projectId: event.target.value })}
              required
            >
              <option value="">Choose a Project</option>
              {projects.map((project) => (
                <option key={project.id} value={project.id}>
                  {project.name}
                </option>
              ))}
            </select>
          </Field>
          <Field label="Skill">
            <input
              value={value.skill}
              onChange={(event) => onChange({ skill: event.target.value })}
            />
          </Field>
          <Field label="Permission mode">
            <select
              value={value.permissionMode}
              onChange={(event) => onChange({ permissionMode: event.target.value })}
            >
              <option value="0">Full access</option>
              <option value="1">Always ask</option>
              <option value="2">Allow same arguments</option>
            </select>
          </Field>
          <Field label="Ask first" hint="Tool names that require a decision before execution.">
            <input
              value={value.askFirst}
              onChange={(event) => onChange({ askFirst: event.target.value })}
            />
          </Field>
          <Field label="Trusted instructions">
            <textarea
              rows={5}
              value={value.instructions}
              onChange={(event) => onChange({ instructions: event.target.value })}
            />
          </Field>
          <Field
            label="Data scope (JSON)"
            hint="Event type keys map to the JSONPath fields kept for that type."
          >
            <textarea
              className="tp-code-input"
              rows={5}
              value={value.dataScope}
              onChange={(event) => onChange({ dataScope: event.target.value })}
              spellCheck={false}
            />
          </Field>
        </div>
      </section>

      <section className="tp-form-section">
        <h3>Coordination and notifications</h3>
        <div className="tp-form-grid">
          <Field label="Group key" hint="Rules in a group share the same concurrency limit.">
            <input
              value={value.groupKey}
              onChange={(event) => onChange({ groupKey: event.target.value })}
            />
          </Field>
          <Field label="Maximum concurrency">
            <input
              type="number"
              min="1"
              max="64"
              value={value.maxConcurrency}
              onChange={(event) => onChange({ maxConcurrency: event.target.value })}
            />
          </Field>
          <Field label="Session policy">
            <select
              value={value.sessionPolicy}
              onChange={(event) => onChange({ sessionPolicy: event.target.value })}
            >
              <option value="0">Per matter</option>
              <option value="1">Per round</option>
            </select>
          </Field>
          <Field label="Notification policy">
            <select
              value={value.notifyPolicy}
              onChange={(event) => onChange({ notifyPolicy: event.target.value })}
            >
              <option value="0">Never</option>
              <option value="1">On attention</option>
              <option value="2">Every round</option>
            </select>
          </Field>
          <Field label="Notification channels" hint="Channel keys, comma separated.">
            <input
              value={value.channels}
              onChange={(event) => onChange({ channels: event.target.value })}
            />
          </Field>
          <Field label="Emitted event types" hint="Only taskpilot.* types are accepted.">
            <textarea
              rows={3}
              value={value.emitTypes}
              onChange={(event) => onChange({ emitTypes: event.target.value })}
            />
          </Field>
        </div>
      </section>

      <section className="tp-form-section">
        <h3>Retry and delivery</h3>
        <div className="tp-form-grid">
          <Field
            label="Retry policy (JSON)"
            hint="Delay minutes, maximum delay, notification threshold and stop threshold."
          >
            <textarea
              className="tp-code-input"
              rows={5}
              value={value.retry}
              onChange={(event) => onChange({ retry: event.target.value })}
              spellCheck={false}
            />
          </Field>
          <Field label="Delivery configuration (JSON)" hint="Used by Delivery rules.">
            <textarea
              className="tp-code-input"
              rows={5}
              value={value.delivery}
              onChange={(event) => onChange({ delivery: event.target.value })}
              spellCheck={false}
            />
          </Field>
        </div>
      </section>

      <div className="tp-form-actions">
        <button type="button" onClick={onCancel} disabled={disabled}>
          Close
        </button>
        <button className="primary" type="submit" disabled={disabled}>
          {disabled ? "Saving…" : editing ? "Save changes" : "Create rule"}
        </button>
      </div>
    </form>
  );
}

export function TaskPilotApp({ extension }: { extension: ExtensionApi }) {
  const api = React.useMemo(() => createTaskPilotApi(extension), [extension]);
  const [section, setSection] = React.useState<Section>("rules");
  const [serverName, setServerName] = React.useState(extension.context.get().server.name);
  const [rules, setRules] = React.useState<TaskPilotRuleSummary[]>([]);
  const [matters, setMatters] = React.useState<TaskPilotMatterSummary[]>([]);
  const [ruleDetails, setRuleDetails] = React.useState<TaskPilotRuleDetails | null>(null);
  const [loadingRuleDetails, setLoadingRuleDetails] = React.useState(false);
  const [matterDetails, setMatterDetails] = React.useState<TaskPilotMatterDetails | null>(null);
  const [rounds, setRounds] = React.useState<TaskPilotRoundSummary[]>([]);
  const [roundDetails, setRoundDetails] = React.useState<TaskPilotRoundDetails | null>(null);
  const [reviews, setReviews] = React.useState<TaskPilotReviewSummary[]>([]);
  const [deliveries, setDeliveries] = React.useState<TaskPilotDeliverySummary[]>([]);
  const [selectedRuleId, setSelectedRuleId] = React.useState<string | null>(null);
  const [selectedMatterId, setSelectedMatterId] = React.useState<string | null>(null);
  const [selectedRoundId, setSelectedRoundId] = React.useState<string | null>(null);
  const [matterRuleFilter, setMatterRuleFilter] = React.useState("");
  const [ruleFormVisible, setRuleFormVisible] = React.useState(false);
  const [ruleForm, setRuleForm] = React.useState<RuleFormState>(EMPTY_RULE_FORM);
  const [busyAction, setBusyAction] = React.useState<string | null>(null);
  const [loading, setLoading] = React.useState(true);
  const [feedback, setFeedback] = React.useState<Feedback | null>(null);
  const [answerOptions, setAnswerOptions] = React.useState<Record<string, string>>({});
  const [answerTexts, setAnswerTexts] = React.useState<Record<string, string>>({});
  const [submittedCardIds, setSubmittedCardIds] = React.useState<ReadonlySet<string>>(
    () => new Set(),
  );
  const [projects, setProjects] = React.useState<ContextProject[]>([]);
  const [targets, setTargets] = React.useState<ContextTarget[]>([]);
  const context = extension.context;
  const matterLoadId = React.useRef(0);
  const roundLoadId = React.useRef(0);

  const refreshContextOptions = React.useCallback(async () => {
    const [nextProjects, nextTargets] = await Promise.all([
      context.listProjects(),
      context.listTargets(),
    ]);
    setProjects(nextProjects);
    setTargets(nextTargets);
  }, [context]);

  React.useEffect(() => {
    const subscription = context.onDidChange((next) => {
      setServerName(next.server.name);
      void refreshContextOptions().catch((error: unknown) =>
        setFeedback({ kind: "error", message: errorText(error) }),
      );
    });
    void refreshContextOptions().catch((error: unknown) =>
      setFeedback({ kind: "error", message: errorText(error) }),
    );
    return () => subscription.dispose();
  }, [context, refreshContextOptions]);

  const refreshLists = React.useCallback(async () => {
    const [nextRules, nextMatters] = await Promise.all([
      api.listRules(),
      api.listMatters(matterRuleFilter || undefined),
    ]);
    setRules(nextRules);
    setMatters(nextMatters);
    setSelectedRuleId((current) =>
      current && nextRules.some((rule) => rule.id === current) ? current : null,
    );
    setSelectedMatterId((current) =>
      current && nextMatters.some((matter) => matter.id === current)
        ? current
        : (nextMatters[0]?.id ?? null),
    );
  }, [api, matterRuleFilter]);

  const loadMatterData = React.useCallback(
    async (matterId: string) => {
      const requestId = ++matterLoadId.current;
      const [nextDetails, nextRounds, nextReviews, nextDeliveries] = await Promise.all([
        api.getMatter(matterId),
        api.listRounds(matterId),
        api.listReviews(matterId),
        api.getDeliveries(matterId),
      ]);
      if (requestId !== matterLoadId.current) return;
      setMatterDetails(nextDetails);
      setRounds(nextRounds);
      setReviews(nextReviews);
      setDeliveries(nextDeliveries);
      setSelectedRoundId((current) =>
        current && nextRounds.some((round) => round.id === current)
          ? current
          : (nextRounds[0]?.id ?? null),
      );
      setSubmittedCardIds((current) => {
        const openNotificationIds = new Set(
          nextDetails.cards.filter((card) => card.state === 0).map((card) => card.id),
        );
        return new Set([...current].filter((id) => openNotificationIds.has(id)));
      });
    },
    [api],
  );

  React.useEffect(() => {
    let mounted = true;
    void refreshLists()
      .catch((error: unknown) => {
        if (mounted) setFeedback({ kind: "error", message: errorText(error) });
      })
      .finally(() => {
        if (mounted) setLoading(false);
      });
    return () => {
      mounted = false;
    };
  }, [refreshLists]);

  React.useEffect(() => {
    if (!selectedRuleId) {
      setRuleDetails(null);
      setLoadingRuleDetails(false);
      return;
    }
    let mounted = true;
    setLoadingRuleDetails(true);
    void api
      .getRule(selectedRuleId)
      .then((details) => {
        if (!mounted) return;
        setRuleDetails(details);
        setRuleForm(formFromRule(details));
      })
      .catch((error: unknown) => {
        if (mounted) setFeedback({ kind: "error", message: errorText(error) });
      })
      .finally(() => {
        if (mounted) setLoadingRuleDetails(false);
      });
    return () => {
      mounted = false;
    };
  }, [api, selectedRuleId]);

  React.useEffect(() => {
    if (!selectedMatterId) {
      matterLoadId.current++;
      setMatterDetails(null);
      setRounds([]);
      setReviews([]);
      setDeliveries([]);
      setSelectedRoundId(null);
      return;
    }
    void loadMatterData(selectedMatterId).catch((error: unknown) =>
      setFeedback({ kind: "error", message: errorText(error) }),
    );
  }, [loadMatterData, selectedMatterId]);

  React.useEffect(() => {
    if (!selectedRoundId) {
      roundLoadId.current++;
      setRoundDetails(null);
      return;
    }
    let mounted = true;
    const requestId = ++roundLoadId.current;
    void api
      .getRound(selectedRoundId)
      .then((details) => {
        if (mounted && requestId === roundLoadId.current) setRoundDetails(details);
      })
      .catch((error: unknown) => {
        if (mounted) setFeedback({ kind: "error", message: errorText(error) });
      });
    return () => {
      mounted = false;
    };
  }, [api, selectedRoundId]);

  const execute = React.useCallback(
    async <T,>(
      label: string,
      successMessage: string,
      operation: () => Promise<T>,
    ): Promise<T | null> => {
      setBusyAction(label);
      setFeedback(null);
      try {
        const result = await operation();
        setFeedback({ kind: "success", message: successMessage });
        extension.ui.notify({ type: "success", message: successMessage });
        try {
          await refreshLists();
          if (selectedMatterId) await loadMatterData(selectedMatterId);
        } catch (error) {
          setFeedback({
            kind: "error",
            message: `The action completed, but refresh failed: ${errorText(error)}`,
          });
        }
        return result;
      } catch (error) {
        const message = errorText(error);
        setFeedback({ kind: "error", message });
        extension.ui.notify({ type: "error", message });
        return null;
      } finally {
        setBusyAction(null);
      }
    },
    [extension, loadMatterData, refreshLists, selectedMatterId],
  );

  async function confirm(message: string): Promise<boolean> {
    const result = await extension.dialog.showMessageBox({
      type: "warning",
      message,
      buttons: ["Keep", "Continue"],
      defaultId: 0,
      cancelId: 0,
    });
    return result === 1;
  }

  async function saveRule(event: React.FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault();
    let request: TaskPilotRuleWriteRequest;
    try {
      request = requestFromRuleForm(ruleForm, ruleDetails);
    } catch (error) {
      setFeedback({ kind: "error", message: errorText(error) });
      return;
    }
    const result = ruleDetails
      ? await execute("rule", "Rule updated", () => api.updateRule(request))
      : await execute("rule", "Rule created", () => api.createRule(request));
    if (!result) return;
    setSelectedRuleId(result.rule.id);
    setRuleDetails(result);
    setRuleForm(formFromRule(result));
    setRuleFormVisible(true);
  }

  async function performRuleAction(action: "pause" | "resume" | "delete"): Promise<void> {
    if (!ruleDetails) return;
    if (action === "delete" && !(await confirm(`Delete rule “${ruleDetails.rule.name}”?`))) return;
    const request = { id: ruleDetails.rule.id, version: ruleDetails.rule.version };
    const successMessage = action === "delete" ? "Rule deleted" : `Rule ${action}d`;
    const result = await execute("rule", successMessage, () =>
      action === "pause"
        ? api.pauseRule(request)
        : action === "resume"
          ? api.resumeRule(request)
          : api.deleteRule(request),
    );
    if (result) {
      setRuleDetails(result);
      setSelectedRuleId(result.rule.id);
      if (action === "delete") setRuleFormVisible(false);
    }
  }

  async function performMatterAction(action: "pause" | "resume" | "run" | "cancel"): Promise<void> {
    const matter = matterDetails?.matter;
    if (!matter) return;
    if (action === "cancel" && !(await confirm(`Cancel the active round for “${matter.title}”?`)))
      return;
    const request = { id: matter.id, version: matter.version };
    const successMessage =
      action === "run"
        ? "Matter run queued"
        : action === "cancel"
          ? "Matter cancellation requested"
          : `Matter ${action}d`;
    await execute("matter", successMessage, async () => {
      if (action === "pause") await api.pauseMatter(request);
      else if (action === "resume") await api.resumeMatter(request);
      else if (action === "run") await api.runMatter(request);
      else
        await api.cancelMatter({
          ...request,
          ...(matter.activeRoundId ? { roundId: matter.activeRoundId } : {}),
        });
    });
  }

  async function answerCard(card: TaskPilotCardSummary): Promise<void> {
    if (!card.notificationId) return;
    const choice = answerOptions[card.id] ?? "";
    const option = choice === "" ? null : Number(choice);
    const text = card.allowText ? answerTexts[card.id]?.trim() || null : null;
    if (option === null && text === null) {
      setFeedback({ kind: "error", message: "Choose an option or enter an answer." });
      return;
    }
    const result = await execute(
      "answer",
      "Answer submitted; the card will update after TaskPilot processes it.",
      () => api.answerNotification({ notificationId: card.notificationId!, option, text }),
    );
    if (result) {
      setSubmittedCardIds((current) => new Set(current).add(card.id));
      setAnswerOptions((current) => ({ ...current, [card.id]: "" }));
      setAnswerTexts((current) => ({ ...current, [card.id]: "" }));
    }
  }

  async function cancelReview(review: TaskPilotReviewSummary): Promise<void> {
    if (!(await confirm(`Cancel review “${review.reviewKey}”?`))) return;
    await execute("review", "Review cancelled", () =>
      api.cancelReview({ id: review.id, version: review.version }),
    );
  }

  const selectedMatter = matterDetails?.matter ?? null;
  const selectedRule = ruleDetails?.rule ?? null;

  return (
    <main className="tp-shell">
      <header className="tp-header">
        <div>
          <p className="tp-eyebrow">TaskPilot · Control plane</p>
          <h1>Operations</h1>
          <p>Manage rules, follow each matter, and review execution and delivery history.</p>
        </div>
        <div className="tp-header-actions">
          <span className="tp-server">{serverName}</span>
          <button
            type="button"
            onClick={() =>
              void refreshLists()
                .then(() => (selectedMatterId ? loadMatterData(selectedMatterId) : undefined))
                .catch((error: unknown) =>
                  setFeedback({ kind: "error", message: errorText(error) }),
                )
            }
          >
            Refresh
          </button>
        </div>
      </header>

      <nav className="tp-tabs" aria-label="TaskPilot pages">
        {SECTIONS.map((item) => (
          <button
            key={item.id}
            type="button"
            className={section === item.id ? "active" : ""}
            aria-current={section === item.id ? "page" : undefined}
            onClick={() => setSection(item.id)}
          >
            {item.label}
            {item.id === "matters" ? <span className="tp-tab-count">{matters.length}</span> : null}
          </button>
        ))}
      </nav>

      {feedback ? (
        <div
          className={`tp-feedback ${feedback.kind}`}
          role={feedback.kind === "error" ? "alert" : "status"}
        >
          {feedback.message}
        </div>
      ) : null}

      {loading ? (
        <section className="tp-empty" role="status">
          Loading TaskPilot…
        </section>
      ) : section === "rules" ? (
        <div className="tp-layout tp-rules-layout">
          <aside className="tp-panel tp-list-panel">
            <div className="tp-panel-heading">
              <div>
                <p className="tp-eyebrow">Configuration</p>
                <h2>
                  Rules <span className="tp-count">{rules.length}</span>
                </h2>
              </div>
              <button
                className="primary"
                type="button"
                onClick={() => {
                  setSelectedRuleId(null);
                  setRuleDetails(null);
                  setRuleForm(EMPTY_RULE_FORM);
                  setRuleFormVisible(true);
                  setFeedback(null);
                }}
              >
                New rule
              </button>
            </div>
            {rules.length === 0 ? (
              <div className="tp-empty compact">No rules configured yet.</div>
            ) : (
              <ul className="tp-list">
                {rules.map((rule) => (
                  <li key={rule.id}>
                    <button
                      className={`tp-list-item ${selectedRuleId === rule.id ? "selected" : ""}`}
                      type="button"
                      onClick={() => {
                        setSelectedRuleId(rule.id);
                        setRuleDetails(null);
                        setRuleFormVisible(true);
                        setFeedback(null);
                      }}
                    >
                      <span className="tp-list-item-main">
                        <strong>{rule.name}</strong>
                        <span className="tp-list-meta">
                          <Badge>{enumLabel(rule.kind, RULE_KINDS)}</Badge>
                          <Badge tone={rule.enabled && !rule.paused ? "success" : "muted"}>
                            {rule.deletedAt
                              ? "Deleted"
                              : rule.paused
                                ? "Paused"
                                : rule.enabled
                                  ? "Enabled"
                                  : "Disabled"}
                          </Badge>
                        </span>
                        <span className="tp-list-meta">
                          <RuleStatus rule={rule} />
                        </span>
                      </span>
                      <span className="tp-row-chevron" aria-hidden="true">
                        ›
                      </span>
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </aside>

          <section className="tp-panel tp-content-panel">
            {ruleFormVisible && loadingRuleDetails ? (
              <div className="tp-empty" role="status">
                Loading rule configuration…
              </div>
            ) : ruleFormVisible ? (
              <>
                {selectedRule && !selectedRule.deletedAt ? (
                  <div className="tp-action-strip">
                    <RuleStatus rule={selectedRule} />
                    {selectedRule.subscriptionId ? (
                      <code>{selectedRule.subscriptionId}</code>
                    ) : null}
                    {selectedRule.subscriptionError ? (
                      <span className="tp-error-inline">{selectedRule.subscriptionError}</span>
                    ) : null}
                    <span className="tp-spacer" />
                    <button
                      type="button"
                      disabled={busyAction !== null}
                      onClick={() =>
                        void performRuleAction(selectedRule.paused ? "resume" : "pause")
                      }
                    >
                      {selectedRule.paused ? "Resume" : "Pause"}
                    </button>
                    <button
                      className="danger"
                      type="button"
                      disabled={busyAction !== null}
                      onClick={() => void performRuleAction("delete")}
                    >
                      Delete
                    </button>
                  </div>
                ) : null}
                <RuleForm
                  value={ruleForm}
                  onChange={(change) => setRuleForm((current) => ({ ...current, ...change }))}
                  onSubmit={(event) => void saveRule(event)}
                  onCancel={() => setRuleFormVisible(false)}
                  editing={ruleDetails !== null}
                  disabled={
                    busyAction !== null || loadingRuleDetails || Boolean(selectedRule?.deletedAt)
                  }
                  projects={projects}
                  targets={targets}
                />
              </>
            ) : selectedRule ? (
              <div className="tp-empty">
                <h2>{selectedRule.name}</h2>
                <p>Select the rule to edit its configuration.</p>
                <RuleStatus rule={selectedRule} />
              </div>
            ) : (
              <div className="tp-empty">
                <h2>Choose a rule</h2>
                <p>
                  Create an event rule or select a rule to review its source and managed
                  subscription.
                </p>
              </div>
            )}
          </section>
        </div>
      ) : section === "matters" ? (
        <div className="tp-layout tp-data-layout">
          <aside className="tp-panel tp-list-panel">
            <div className="tp-panel-heading">
              <div>
                <p className="tp-eyebrow">Tracked work</p>
                <h2>
                  Matters <span className="tp-count">{matters.length}</span>
                </h2>
              </div>
              <select
                aria-label="Filter matters by rule"
                value={matterRuleFilter}
                onChange={(event) => {
                  setMatterRuleFilter(event.target.value);
                }}
              >
                <option value="">All rules</option>
                {rules
                  .filter((rule) => !rule.deletedAt)
                  .map((rule) => (
                    <option key={rule.id} value={rule.id}>
                      {rule.name}
                    </option>
                  ))}
              </select>
            </div>
            {matters.length === 0 ? (
              <div className="tp-empty compact">No matters yet.</div>
            ) : (
              <ul className="tp-list">
                {matters.map((matter) => (
                  <li key={matter.id}>
                    <button
                      className={`tp-list-item ${selectedMatterId === matter.id ? "selected" : ""}`}
                      type="button"
                      onClick={() => setSelectedMatterId(matter.id)}
                    >
                      <span className="tp-list-item-main">
                        <strong>{matter.title || matter.matterKey}</strong>
                        <span className="tp-list-meta">
                          <Badge
                            tone={
                              matter.state === 0
                                ? "success"
                                : matter.state === 2
                                  ? "warning"
                                  : "muted"
                            }
                          >
                            {enumLabel(matter.state, MATTER_STATES)}
                          </Badge>
                          {matter.queuedRounds > 0 ? (
                            <Badge>{matter.queuedRounds} queued</Badge>
                          ) : null}
                        </span>
                        <span className="tp-list-description">{matter.matterKey}</span>
                      </span>
                      <span className="tp-row-chevron" aria-hidden="true">
                        ›
                      </span>
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </aside>
          <section className="tp-panel tp-content-panel">
            {selectedMatter ? (
              <MatterOverview
                matter={selectedMatter}
                cards={matterDetails?.cards ?? []}
                busy={busyAction !== null}
                submittedCardIds={submittedCardIds}
                answerOptions={answerOptions}
                answerTexts={answerTexts}
                onAction={(action) => void performMatterAction(action)}
                onAnswer={(card) => void answerCard(card)}
                onOptionChange={(id, value) =>
                  setAnswerOptions((current) => ({ ...current, [id]: value }))
                }
                onTextChange={(id, value) =>
                  setAnswerTexts((current) => ({ ...current, [id]: value }))
                }
              />
            ) : (
              <div className="tp-empty">
                <h2>No matter selected</h2>
                <p>Matters appear when a TaskPilot rule accepts an event.</p>
              </div>
            )}
          </section>
        </div>
      ) : section === "rounds" ? (
        <RoundsPanel
          matter={selectedMatter}
          rounds={rounds}
          selectedRoundId={selectedRoundId}
          details={roundDetails}
          onSelect={setSelectedRoundId}
        />
      ) : section === "reviews" ? (
        <ReviewsPanel
          reviews={reviews}
          busy={busyAction !== null}
          onCancel={(review) => void cancelReview(review)}
        />
      ) : (
        <DeliveriesPanel deliveries={deliveries} />
      )}
    </main>
  );
}

function MatterOverview({
  matter,
  cards,
  busy,
  submittedCardIds,
  answerOptions,
  answerTexts,
  onAction,
  onAnswer,
  onOptionChange,
  onTextChange,
}: {
  matter: TaskPilotMatterSummary;
  cards: TaskPilotCardSummary[];
  busy: boolean;
  submittedCardIds: ReadonlySet<string>;
  answerOptions: Record<string, string>;
  answerTexts: Record<string, string>;
  onAction: (action: "pause" | "resume" | "run" | "cancel") => void;
  onAnswer: (card: TaskPilotCardSummary) => void;
  onOptionChange: (id: string, value: string) => void;
  onTextChange: (id: string, value: string) => void;
}) {
  const canRun = matter.state === 0 || matter.state === 1;
  const canPause = matter.state === 0 || matter.state === 1;
  return (
    <div className="tp-detail-stack">
      <div className="tp-detail-heading">
        <div>
          <p className="tp-eyebrow">Matter</p>
          <h2>{matter.title || matter.matterKey}</h2>
          <p>
            <code>{matter.matterKey}</code>
          </p>
        </div>
        <div className="tp-actions">
          <Badge tone={matter.state === 0 ? "success" : matter.state === 2 ? "warning" : "muted"}>
            {enumLabel(matter.state, MATTER_STATES)}
          </Badge>
          {canPause ? (
            <button type="button" disabled={busy} onClick={() => onAction("pause")}>
              Pause
            </button>
          ) : null}
          {matter.state === 2 ? (
            <button type="button" disabled={busy} onClick={() => onAction("resume")}>
              Resume
            </button>
          ) : null}
          <button type="button" disabled={busy || !canRun} onClick={() => onAction("run")}>
            Run now
          </button>
          {matter.activeRoundId ? (
            <button
              className="danger"
              type="button"
              disabled={busy}
              onClick={() => onAction("cancel")}
            >
              Cancel active round
            </button>
          ) : null}
        </div>
      </div>

      <div className="tp-stat-grid">
        <Stat label="Queued rounds" value={matter.queuedRounds} />
        <Stat label="Consecutive failures" value={matter.consecutiveFailures} />
        <Stat label="Version" value={matter.version} />
        <Stat
          label="Conversation"
          value={matter.conversationId ? matter.conversationId.slice(0, 8) : "None"}
        />
      </div>

      <section className="tp-section-card">
        <div className="tp-section-heading">
          <div>
            <p className="tp-eyebrow">Human decisions</p>
            <h3>
              Cards <span className="tp-count">{cards.length}</span>
            </h3>
          </div>
          <p>Answers are recorded by Notifications and flow back through the event system.</p>
        </div>
        {cards.length === 0 ? (
          <div className="tp-empty compact">No question or notice cards.</div>
        ) : (
          <div className="tp-card-grid">
            {cards.map((card) => {
              const submitted = submittedCardIds.has(card.id);
              const selected = answerOptions[card.id] ?? "";
              const answerText = answerTexts[card.id] ?? "";
              const canAnswer =
                card.kind === 0 && card.state === 0 && Boolean(card.notificationId) && !submitted;
              const hasAnswer = selected !== "" || (card.allowText && answerText.trim().length > 0);
              return (
                <article className="tp-question-card" key={card.id}>
                  <div className="tp-card-topline">
                    <Badge tone={card.kind === 0 ? "accent" : "muted"}>
                      {enumLabel(card.kind, CARD_KINDS)}
                    </Badge>
                    <Badge tone={card.state === 0 ? "warning" : "success"}>
                      {enumLabel(card.state, CARD_STATES)}
                    </Badge>
                  </div>
                  <h4>{card.title}</h4>
                  <p className="tp-card-text">{card.text}</p>
                  {card.options.length > 0 ? (
                    <fieldset className="tp-options" disabled={!canAnswer || busy}>
                      <legend>Choose an option</legend>
                      {card.options.map((option, index) => (
                        <label className="tp-option" key={`${card.id}-${option.value}`}>
                          <input
                            type="radio"
                            name={`answer-${card.id}`}
                            value={index}
                            checked={selected === String(index)}
                            onChange={() => onOptionChange(card.id, String(index))}
                          />
                          <span>{option.label}</span>
                          {card.recommend === index ? (
                            <Badge tone="accent">Recommended</Badge>
                          ) : null}
                        </label>
                      ))}
                    </fieldset>
                  ) : null}
                  {card.allowText && canAnswer ? (
                    <label className="tp-field">
                      <span className="tp-field-label">Additional answer</span>
                      <textarea
                        rows={3}
                        value={answerText}
                        onChange={(event) => onTextChange(card.id, event.target.value)}
                      />
                    </label>
                  ) : null}
                  {card.answer !== null ? (
                    <details className="tp-json-details">
                      <summary>Recorded answer</summary>
                      <pre>{prettyJson(card.answer)}</pre>
                    </details>
                  ) : null}
                  {card.resolvedReason ? <p className="tp-note">{card.resolvedReason}</p> : null}
                  {card.state === 0 ? (
                    <div className="tp-card-footer">
                      {!card.notificationId ? (
                        <span className="tp-note">Notification is being prepared.</span>
                      ) : null}
                      {submitted ? (
                        <span className="tp-note">Answer submitted; awaiting card update.</span>
                      ) : null}
                      {canAnswer ? (
                        <button
                          className="primary"
                          type="button"
                          disabled={busy || !hasAnswer}
                          onClick={() => onAnswer(card)}
                        >
                          Submit answer
                        </button>
                      ) : null}
                    </div>
                  ) : null}
                </article>
              );
            })}
          </div>
        )}
      </section>
    </div>
  );
}

function RoundsPanel({
  matter,
  rounds,
  selectedRoundId,
  details,
  onSelect,
}: {
  matter: TaskPilotMatterSummary | null;
  rounds: TaskPilotRoundSummary[];
  selectedRoundId: string | null;
  details: TaskPilotRoundDetails | null;
  onSelect: (id: string) => void;
}) {
  if (!matter)
    return (
      <div className="tp-panel tp-empty">
        <h2>Select a matter</h2>
        <p>Round history is grouped by matter.</p>
      </div>
    );
  const round = details?.round;
  return (
    <div className="tp-layout tp-data-layout">
      <aside className="tp-panel tp-list-panel">
        <div className="tp-panel-heading">
          <div>
            <p className="tp-eyebrow">{matter.title || matter.matterKey}</p>
            <h2>
              Rounds <span className="tp-count">{rounds.length}</span>
            </h2>
          </div>
        </div>
        {rounds.length === 0 ? (
          <div className="tp-empty compact">No rounds have started.</div>
        ) : (
          <ul className="tp-list">
            {rounds.map((item) => (
              <li key={item.id}>
                <button
                  className={`tp-list-item ${selectedRoundId === item.id ? "selected" : ""}`}
                  type="button"
                  onClick={() => onSelect(item.id)}
                >
                  <span className="tp-list-item-main">
                    <strong>{enumLabel(item.state, ROUND_STATES)}</strong>
                    <span className="tp-list-meta">
                      <Badge>{enumLabel(item.trigger, ROUND_TRIGGERS)}</Badge>
                      <Badge tone="muted">Attempt {item.attempt}</Badge>
                    </span>
                    <span className="tp-list-description">
                      {item.summary || item.error || dateLabel(item.startedAt)}
                    </span>
                  </span>
                  <span className="tp-row-chevron" aria-hidden="true">
                    ›
                  </span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </aside>
      <section className="tp-panel tp-content-panel">
        {round ? (
          <div className="tp-detail-stack">
            <div className="tp-detail-heading">
              <div>
                <p className="tp-eyebrow">Round</p>
                <h2>{enumLabel(round.state, ROUND_STATES)}</h2>
                <p>
                  <code>{round.id}</code>
                </p>
              </div>
              <div className="tp-actions">
                <Badge>{enumLabel(round.trigger, ROUND_TRIGGERS)}</Badge>
                <Badge tone="muted">Attempt {round.attempt}</Badge>
              </div>
            </div>
            <div className="tp-stat-grid">
              <Stat label="Input tokens" value={round.inputTokens} />
              <Stat label="Output tokens" value={round.outputTokens} />
              <Stat label="Started" value={dateLabel(round.startedAt)} />
              <Stat label="Ended" value={dateLabel(round.endedAt)} />
            </div>
            {round.summary ? (
              <section className="tp-section-card">
                <h3>Conclusion</h3>
                <p className="tp-body-copy">{round.summary}</p>
              </section>
            ) : null}
            {round.error || round.receiptError ? (
              <section className="tp-section-card tp-error-card">
                <h3>Errors</h3>
                {round.error ? <p>{round.error}</p> : null}
                {round.receiptError ? <p>{round.receiptError}</p> : null}
              </section>
            ) : null}
            <section className="tp-section-card">
              <div className="tp-section-heading">
                <div>
                  <p className="tp-eyebrow">Round inputs</p>
                  <h3>
                    Events <span className="tp-count">{details.inputs.length}</span>
                  </h3>
                </div>
              </div>
              {details.inputs.length === 0 ? (
                <p className="tp-note">No input snapshots were recorded.</p>
              ) : (
                <div className="tp-timeline">
                  {details.inputs.map((input) => (
                    <article className="tp-timeline-item" key={input.eventId}>
                      <div className="tp-timeline-marker" />
                      <div className="tp-timeline-content">
                        <div className="tp-card-topline">
                          <strong>{enumLabel(input.kind, ROUND_INPUT_KINDS)}</strong>
                          <span>{dateLabel(input.receivedAt)}</span>
                        </div>
                        {input.summary ? <p>{input.summary}</p> : null}
                        <p className="tp-note">
                          Event {input.eventId} · {input.hops} hops
                        </p>
                        {input.event !== null ? (
                          <details className="tp-json-details">
                            <summary>Event snapshot</summary>
                            <pre>{prettyJson(input.event)}</pre>
                          </details>
                        ) : null}
                        {input.payload !== null ? (
                          <details className="tp-json-details">
                            <summary>Input payload</summary>
                            <pre>{prettyJson(input.payload)}</pre>
                          </details>
                        ) : null}
                      </div>
                    </article>
                  ))}
                </div>
              )}
            </section>
            <section className="tp-section-card">
              <div className="tp-section-heading">
                <div>
                  <p className="tp-eyebrow">Execution attempts</p>
                  <h3>
                    Attempts <span className="tp-count">{details.attempts.length}</span>
                  </h3>
                </div>
              </div>
              {details.attempts.length === 0 ? (
                <p className="tp-note">No attempt records.</p>
              ) : (
                <div className="tp-attempt-list">
                  {details.attempts.map((attempt) => (
                    <article className="tp-attempt" key={`${attempt.attempt}-${attempt.turnId}`}>
                      <div className="tp-card-topline">
                        <strong>Attempt {attempt.attempt}</strong>
                        <Badge>
                          {enumLabel(attempt.state, [
                            "Submitting",
                            "Running",
                            "Completed",
                            "Failed",
                            "Cancelled",
                          ])}
                        </Badge>
                      </div>
                      <p className="tp-note">
                        {enumLabel(attempt.purpose, ["Run", "Receipt repair", "Delivery review"])} ·
                        turn {attempt.turnId}
                      </p>
                      <div className="tp-inline-stats">
                        <span>{attempt.inputTokens} input tokens</span>
                        <span>{attempt.outputTokens} output tokens</span>
                        <span>
                          {dateLabel(attempt.startedAt)} – {dateLabel(attempt.endedAt)}
                        </span>
                      </div>
                      {attempt.error ? <p className="tp-error-inline">{attempt.error}</p> : null}
                    </article>
                  ))}
                </div>
              )}
            </section>
            <section className="tp-section-card">
              <h3>Receipt</h3>
              <pre className="tp-json-block">{prettyJson(details.receipt)}</pre>
            </section>
          </div>
        ) : (
          <div className="tp-empty">
            <h2>Select a round</h2>
            <p>Round inputs, receipts, attempts, errors and usage appear here.</p>
          </div>
        )}
      </section>
    </div>
  );
}

function ReviewsPanel({
  reviews,
  busy,
  onCancel,
}: {
  reviews: TaskPilotReviewSummary[];
  busy: boolean;
  onCancel: (review: TaskPilotReviewSummary) => void;
}) {
  return (
    <section className="tp-panel tp-content-panel tp-single-panel">
      <div className="tp-panel-heading">
        <div>
          <p className="tp-eyebrow">Scheduled checks</p>
          <h2>
            Reviews <span className="tp-count">{reviews.length}</span>
          </h2>
          <p>Review requests are tied to the selected matter.</p>
        </div>
      </div>
      {reviews.length === 0 ? (
        <div className="tp-empty">No reviews scheduled for the selected matter.</div>
      ) : (
        <div className="tp-table-wrap">
          <table className="tp-table">
            <thead>
              <tr>
                <th>Review</th>
                <th>Status</th>
                <th>Due</th>
                <th>About</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {reviews.map((review) => (
                <tr key={review.id}>
                  <td>
                    <strong>{review.reviewKey}</strong>
                    <small>
                      Request {review.requestSeq} · v{review.version}
                    </small>
                  </td>
                  <td>
                    <Badge
                      tone={
                        review.state === 2 ? "muted" : review.state === 1 ? "warning" : "success"
                      }
                    >
                      {enumLabel(review.state, REVIEW_STATES)}
                    </Badge>
                  </td>
                  <td>{dateLabel(review.dueAt)}</td>
                  <td>{review.about || "—"}</td>
                  <td className="tp-table-action">
                    {review.state !== 2 ? (
                      <button
                        type="button"
                        className="danger"
                        disabled={busy}
                        onClick={() => onCancel(review)}
                      >
                        Cancel
                      </button>
                    ) : null}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

function DeliveriesPanel({ deliveries }: { deliveries: TaskPilotDeliverySummary[] }) {
  return (
    <section className="tp-panel tp-content-panel tp-single-panel">
      <div className="tp-panel-heading">
        <div>
          <p className="tp-eyebrow">Pull request automation</p>
          <h2>
            Delivery timeline <span className="tp-count">{deliveries.length}</span>
          </h2>
          <p>Delivery records and the latest stage results for the selected matter.</p>
        </div>
      </div>
      {deliveries.length === 0 ? (
        <div className="tp-empty">No PR delivery records for the selected matter.</div>
      ) : (
        <div className="tp-timeline tp-delivery-timeline">
          {deliveries.map((delivery) => (
            <article className="tp-timeline-item" key={delivery.id}>
              <div className="tp-timeline-marker" />
              <div className="tp-timeline-content tp-delivery-card">
                <div className="tp-card-topline">
                  <div>
                    <p className="tp-eyebrow">{delivery.repository}</p>
                    <h3>Pull request #{delivery.pullRequestNumber}</h3>
                  </div>
                  <Badge tone="accent">{delivery.stage}</Badge>
                </div>
                <div className="tp-inline-stats">
                  <span>{delivery.mode === 0 ? "Full delivery" : "Review only"}</span>
                  <span>Version {delivery.version}</span>
                </div>
                <div className="tp-sha-grid">
                  <Sha label="Head" value={delivery.headSha} />
                  <Sha label="Reviewed" value={delivery.reviewedSha} />
                  <Sha label="Merge" value={delivery.mergeSha} />
                </div>
                {delivery.lastError ? (
                  <p className="tp-error-inline">{delivery.lastError}</p>
                ) : null}
              </div>
            </article>
          ))}
        </div>
      )}
    </section>
  );
}

function Sha({ label, value }: { label: string; value: string | null }) {
  return (
    <div className="tp-sha">
      <span>{label}</span>
      <code>{value ?? "Pending"}</code>
    </div>
  );
}

function Stat({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <div className="tp-stat">
      <span>{label}</span>
      <strong>{value}</strong>
    </div>
  );
}
