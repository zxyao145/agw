"use client";

import * as React from "react";
import { apiGet, apiPost, apiPut, ApiError, getApiErrorMessage } from "@agw/api";
import { toast } from "sonner";

import type { AgentflowRevisionDto } from "../../../../types/agentflow";
import { createEditorDocument, replaceEditorGraph } from "./agentflow-document";
import {
  getAgentflowDocumentFingerprint,
  useAgentflowEditorStore,
  useAgentflowEditorStoreApi,
  type AgentflowEditorState,
} from "./agentflow-editor-store";
import { hasPassedValidation } from "./agentflow-problems";
import { createAgentflowDefinition } from "./agentflow-transfer";

const AGENTFLOW_DEFINITION_INVALID_CODE = 4000080;
const AGENTFLOW_REVISION_CONFLICT_CODE = 4090038;

export function useAgentflowRevisionActions(onSaved?: () => void) {
  const store = useAgentflowEditorStoreApi();
  const { revisions, refreshRevisions } = useRevisionHistory();
  const [conflictRevision, setConflictRevision] = React.useState<number | null>(null);
  const save = (baseRevision?: number) =>
    saveRevision(store.getState(), {
      baseRevision,
      onSaved,
      refreshRevisions,
      onConflict: setConflictRevision,
    });
  const open = (revision: number, reload = false) =>
    openRevision(store.getState(), revision, reload);

  async function run() {
    const state = store.getState();
    if (!canSaveOrRun(state)) return;
    if ((!state.agentflowId || state.isDirty) && !(await save())) return;
    state.showPanel("run");
  }

  return {
    revisions,
    conflictRevision,
    closeConflict: () => setConflictRevision(null),
    publish: (revision: number) =>
      publishRevision(store.getState(), revision, onSaved, refreshRevisions),
    open,
    save,
    run,
  };
}

function useRevisionHistory() {
  const agentflowId = useAgentflowEditorStore((state) => state.agentflowId);
  const [revisions, setRevisions] = React.useState<AgentflowRevisionDto[]>([]);
  const sequence = React.useRef(0);
  const refreshRevisions = React.useCallback(async (id: string) => {
    const request = ++sequence.current;
    try {
      const result = await apiGet("/api/agentflows/revisions", { params: { query: { id } } });
      if (!result) throw new Error("Agentflow revisions returned no result.");
      if (request === sequence.current) setRevisions(result);
      return result;
    } catch (error) {
      if (request === sequence.current) toast.error(getApiErrorMessage(error));
      return null;
    }
  }, []);

  React.useEffect(() => {
    setRevisions([]);
    if (agentflowId) void refreshRevisions(agentflowId);
    return () => {
      sequence.current++;
    };
  }, [agentflowId, refreshRevisions]);

  return { revisions, refreshRevisions };
}

type SaveRevisionOptions = {
  baseRevision?: number;
  onSaved?: () => void;
  refreshRevisions: ReturnType<typeof useRevisionHistory>["refreshRevisions"];
  onConflict(revision: number): void;
};

async function saveRevision(state: AgentflowEditorState, options: SaveRevisionOptions) {
  if (!canSaveOrRun(state)) return null;
  state.setSaving(true);
  try {
    const saved = await postRevision(state, options.baseRevision ?? state.baseRevision);
    state.markSaved({ agentflowId: saved.id, latestRevision: saved.latestRevision });
    options.onSaved?.();
    toast.success(`Saved v${saved.latestRevision}`);
    await options.refreshRevisions(saved.id);
    return saved;
  } catch (error) {
    await reportSaveError(error, state, options);
    return null;
  } finally {
    state.setSaving(false);
  }
}

function canSaveOrRun(state: AgentflowEditorState): boolean {
  if (state.isSaving) return false;
  if (hasPassedValidation(state.problems, getAgentflowDocumentFingerprint(state.document)))
    return true;
  state.showPanel("problems");
  return false;
}

async function postRevision({ document, agentflowId }: AgentflowEditorState, baseRevision: number) {
  const request = {
    name: document.name,
    description: document.description || null,
    definition: createAgentflowDefinition(document),
  };
  const saved = agentflowId
    ? await apiPost("/api/agentflows/revisions", {
        body: { ...request, agentflowId, baseRevision },
      })
    : await apiPost("/api/agentflows", { body: request });
  if (!saved) throw new Error("Agentflow save returned no result.");
  return saved;
}

async function reportSaveError(
  error: unknown,
  state: AgentflowEditorState,
  options: SaveRevisionOptions,
) {
  const code = getApiResultCode(error);
  if (code === AGENTFLOW_REVISION_CONFLICT_CODE && state.agentflowId) {
    const latest = await options.refreshRevisions(state.agentflowId);
    if (!latest) return;
    const newest = latest[0]?.revision;
    if (newest === undefined) toast.error("The latest revision could not be loaded.");
    else options.onConflict(newest);
  } else if (code === AGENTFLOW_DEFINITION_INVALID_CODE) {
    state.requestValidation();
    state.showPanel("problems");
  } else {
    toast.error(getApiErrorMessage(error));
  }
}

function getApiResultCode(error: unknown): number | undefined {
  if (!(error instanceof ApiError) || typeof error.body !== "object" || error.body === null)
    return undefined;
  const code = (error.body as Record<string, unknown>).code;
  return typeof code === "number" ? code : undefined;
}

async function publishRevision(
  state: AgentflowEditorState,
  revision: number,
  onSaved: (() => void) | undefined,
  refreshRevisions: SaveRevisionOptions["refreshRevisions"],
) {
  const { agentflowId } = state;
  if (!agentflowId || state.isSaving) return;
  state.setSaving(true);
  try {
    const updated = await apiPut("/api/agentflows/published", { body: { agentflowId, revision } });
    if (!updated) throw new Error("Agentflow publication returned no result.");
    state.setPublishedRevision(updated.publishedRevision);
    onSaved?.();
    toast.success(`Published v${revision}`);
    await refreshRevisions(agentflowId);
  } catch (error) {
    toast.error(getApiErrorMessage(error));
  } finally {
    state.setSaving(false);
  }
}

async function openRevision(state: AgentflowEditorState, revision: number, reload: boolean) {
  if (!state.agentflowId || state.isSaving) return false;
  try {
    const detail = await apiGet("/api/agentflows/detail", {
      params: { query: { id: state.agentflowId, revision } },
    });
    if (!detail) throw new Error("Agentflow revision returned no result.");
    const document = createEditorDocument(detail.agentflow, detail.definition);
    if (reload) {
      state.loadDocument({
        document,
        agentflowId: detail.agentflow.id,
        baseRevision: detail.revision,
        publishedRevision: detail.agentflow.publishedRevision,
      });
    } else {
      state.updateDocument((current) => replaceEditorGraph(current, document));
    }
    return true;
  } catch (error) {
    toast.error(getApiErrorMessage(error));
    return false;
  }
}
