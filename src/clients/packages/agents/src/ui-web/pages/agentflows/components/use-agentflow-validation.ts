"use client";

import * as React from "react";
import { apiPost } from "@agw/api";
import type { AgentflowValidateRequest } from "../../../../types/agentflow";
import { getAgentflowDocumentFingerprint, useAgentflowEditorStore } from "./agentflow-editor-store";
import { createProblemsChecker, type ProblemsChecker } from "./agentflow-problems";

export function useAgentflowValidation() {
  const document = useAgentflowEditorStore((state) => state.document);
  const agentflowId = useAgentflowEditorStore((state) => state.agentflowId);
  const requestId = useAgentflowEditorStore((state) => state.validationRequestId);
  const setProblems = useAgentflowEditorStore((state) => state.setProblems);
  const requestValidation = useAgentflowEditorStore((state) => state.requestValidation);
  const checker = React.useRef<ProblemsChecker | null>(null);
  const previousRequestId = React.useRef<number | null>(null);

  React.useEffect(() => {
    const current = createProblemsChecker({ validate, onChange: setProblems });
    checker.current = current;
    previousRequestId.current = null;
    return () => {
      current.dispose();
      if (checker.current === current) checker.current = null;
    };
  }, [setProblems]);

  React.useEffect(() => {
    const fingerprint = getAgentflowDocumentFingerprint(document);
    checker.current?.check(
      { agentflowId, document, fingerprint },
      previousRequestId.current !== requestId,
    );
    previousRequestId.current = requestId;
  }, [agentflowId, document, requestId, setProblems]);

  return requestValidation;
}

async function validate(request: AgentflowValidateRequest, signal: AbortSignal) {
  const response = await apiPost("/api/agentflows/validate", { body: request, signal });
  if (!response) throw new Error("Agentflow validation returned no result.");
  return response.problems;
}
