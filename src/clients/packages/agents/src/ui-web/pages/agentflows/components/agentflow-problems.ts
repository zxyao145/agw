import type { AgentflowProblem, AgentflowValidateRequest } from "../../../../types/agentflow";
import type { AgentflowEditorDocument } from "./agentflow-document";
import { createAgentflowDefinition } from "./agentflow-transfer";

export type AgentflowProblemsState = {
  problems: AgentflowProblem[];
  fingerprint: string | null;
  status: "idle" | "checking" | "failed";
  error: string | null;
};

export const EMPTY_PROBLEMS_STATE: AgentflowProblemsState = {
  problems: [],
  fingerprint: null,
  status: "idle",
  error: null,
};

export type ProblemsChecker = {
  check(
    request: { agentflowId: string | null; document: AgentflowEditorDocument; fingerprint: string },
    immediate?: boolean,
  ): void;
  dispose(): void;
};

export function createProblemsChecker(options: {
  validate: (request: AgentflowValidateRequest, signal: AbortSignal) => Promise<AgentflowProblem[]>;
  onChange: (state: AgentflowProblemsState) => void;
  delayMs?: number;
}): ProblemsChecker {
  const delayMs = options.delayMs ?? 400;
  let state = EMPTY_PROBLEMS_STATE;
  let timer: ReturnType<typeof setTimeout> | undefined;
  let activeRequest: AbortController | undefined;
  let requestId = 0;
  let disposed = false;

  const publish = (nextState: AgentflowProblemsState) => {
    state = nextState;
    options.onChange(state);
  };

  return {
    check: (request, immediate = false) => {
      if (timer) clearTimeout(timer);
      activeRequest?.abort();
      const currentRequestId = ++requestId;
      publish({ ...state, fingerprint: null, status: "checking", error: null });

      const validate = () => {
        const controller = new AbortController();
        activeRequest = controller;
        options
          .validate(
            {
              agentflowId: request.agentflowId,
              name: request.document.name,
              definition: createAgentflowDefinition(request.document),
            },
            controller.signal,
          )
          .then((problems) => {
            if (disposed || controller.signal.aborted || requestId !== currentRequestId) return;
            publish({ problems, fingerprint: request.fingerprint, status: "idle", error: null });
          })
          .catch((error: unknown) => {
            if (disposed || controller.signal.aborted || requestId !== currentRequestId) return;
            publish({
              ...state,
              fingerprint: request.fingerprint,
              status: "failed",
              error: error instanceof Error ? error.message : String(error),
            });
          });
      };

      if (immediate) validate();
      else timer = setTimeout(validate, delayMs);
    },
    dispose: () => {
      disposed = true;
      if (timer) clearTimeout(timer);
      activeRequest?.abort();
    },
  };
}

export function hasPassedValidation(
  state: AgentflowProblemsState,
  currentFingerprint: string,
): boolean {
  return (
    state.status === "idle" &&
    state.fingerprint === currentFingerprint &&
    state.problems.length === 0
  );
}
